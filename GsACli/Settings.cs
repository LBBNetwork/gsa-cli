// Settings - phase 2 settings engine.
//
// Thin binding between the gsa-cli settings schema (settings.xml,
// Decision 40 reference table, schema as in the workspace file) and the
// Xml2 library (Xml2/XmlSettings.cs) - the deterministic reader/writer
// for GSAToolkit-style settings files.
//
// Responsibilities here:
//   - schema -> path mapping ("Defaults/Default", id-attribute lookup,
//     case-insensitive on operator-typed keys per Decision 12)
//   - atomic save (Decision 34/40): Xml2.Save is a truncate-and-write;
//     the atomicity requirement is implemented here as ToXml() ->
//     unique temp file -> overwrite-copy -> temp delete, so a crash
//     mid-write leaves either the old or the complete new file, never a
//     truncated one
//   - corrupt-file recovery support (Decision 28): rename aside +
//     fresh copy happens at startup (Program.cs); this class only offers
//     the rename helper and the diagnostic formatter (Decision 31)
//
// Not here: command semantics (which keys exist, what values they
// accept) - that is the Session layer's grammar, not the engine's.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GsACli
{
	public class Settings
	{
		// Xml2 addressing rules (SettingPaths.cs): paths are
		// root-relative, the root element's own name is NOT a segment,
		// first matching child wins, matching is case-sensitive.
		// Our file:  <Settings><Defaults><Default id="K">V</Default>...
		//                    <Departments><Department number="n">NAME</Department>...
		private const string DefaultsRecordPath = "Defaults/Default";
		private const string DepartmentsRecordPath = "Departments/Department";

		private readonly string path;
		private readonly Xml2.XmlSettings doc;

		// Creates a Settings handle around an already-loaded document
		// and the canonical location of the file on disk.
		private Settings(string path, Xml2.XmlSettings doc)
		{
			this.path = path;
			this.doc = doc;
		}

		// Loads settings.xml from disk. Xml2 reads it as UTF-8 (BOM
		// honored) and parses fail-fast: malformed content throws
		// Xml2.XmlSettingsException; missing/unreadable file throws
		// System.IO.IOException. Both are the caller's to interpret
		// (startup recovery is Program.cs, per Decision 28).
		public static Settings Load(string path)
		{
			return new GsACli.Settings(path, Xml2.XmlSettings.Load(path));
		}

		// Canonical disk location of this settings file (settings.xml
		// next to the program file, per the workspace and Decision 40).
		public string Path
		{
			get { return this.path; }
		}

		// True if the key exists at all. Exists-but-empty is still
		// "exists" (an operator may blank a value); absent is not.
		public bool HasKey(string key)
		{
			return this.FindKeyIndex(key) >= 0;
		}

		// Operator-typed key lookup, case-insensitive (Decision 12:
		// field names are case-insensitive) - returns the file's own
		// spelling and the stored value. The file spelling wins so
		// SHOW CONFIG and CONFIG round-trip to identical bytes.
		public bool TryGet(string key, out string fileKey, out string value)
		{
			fileKey = null;
			value = null;

			StringComparer comparer = StringComparer.OrdinalIgnoreCase;
			int count = this.doc.Count(DefaultsRecordPath);

			for (int i = 0; i < count; i++)
			{
				string id = null;
				if (!this.doc.TryGetRecordAttribute(DefaultsRecordPath, i, "id", out id))
				{
					continue;   // record without an id - not addressable
				}

				if (comparer.Equals(id, key))
				{
					string text = null;
					if (this.doc.TryGetRecordValue(DefaultsRecordPath, i, out text))
					{
						fileKey = id;
						value = text;
						return true;
					}
					// element present but carries no text: still addressable
					fileKey = id;
					value = string.Empty;
					return true;
				}
			}

			return false;
		}

		// Set a key to a raw (unvalidated, unquoted) value. Existing
		// record: update in place (order and spelling preserved). New
		// key: append at the end of Defaults (operator's key spelling,
		// uppercased by the caller as stored form per Decision 12).
		public void Set(string key, string value)
		{
			int index = this.FindKeyIndex(key);

			if (index < 0)
			{
				index = this.doc.AddRecord(
					DefaultsRecordPath,
					new string[] { "id" },
					new string[] { key });
			}

			this.doc.SetRecordValue(DefaultsRecordPath, index, value);
		}

		// Department map: number -> name. Number is a decimal string
		// (1..99, operator-typed, stored as given); names are
		// free-form (Decision 22: department codes and their names).
		public bool TryGetDepartment(string number, out string name)
		{
			name = null;

			int index = this.doc.FindByAttribute(DepartmentsRecordPath, "number", number);
			if (index < 0)
			{
				return false;
			}

			string text = null;
			if (!this.doc.TryGetRecordValue(DepartmentsRecordPath, index, out text))
			{
				// record present but unnamed - treat as empty name
				name = string.Empty;
				return true;
			}

			name = text;
			return true;
		}

		// Upsert: CONFIG DEPARTMENT <n> '<name>' (Decision 34).
		// Existing number: rename. New number: append to Departments.
		public void SetDepartment(string number, string name)
		{
			int index = this.doc.FindByAttribute(DepartmentsRecordPath, "number", number);

			if (index < 0)
			{
				index = this.doc.AddRecord(
					DepartmentsRecordPath,
					new string[] { "number" },
					new string[] { number });
			}

			this.doc.SetRecordValue(DepartmentsRecordPath, index, name);
		}

		// Ordered enumeration for SHOW CONFIG: every Defaults/Default
		// record that carries an id, in file order.
		public List<KeyValuePair<string, string>> EnumerateKeys()
		{
			List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
			int count = this.doc.Count(DefaultsRecordPath);

			for (int i = 0; i < count; i++)
			{
				string id = null;
				if (!this.doc.TryGetRecordAttribute(DefaultsRecordPath, i, "id", out id))
				{
					continue;
				}

				string text = null;
				if (!this.doc.TryGetRecordValue(DefaultsRecordPath, i, out text))
				{
					text = string.Empty;
				}

				result.Add(new KeyValuePair<string, string>(id, text));
			}

			return result;
		}

		// Ordered enumeration for SHOW DEPARTMENTS: every
		// Departments/Department record, in file order.
		public List<KeyValuePair<string, string>> EnumerateDepartments()
		{
			List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
			int count = this.doc.Count(DepartmentsRecordPath);

			for (int i = 0; i < count; i++)
			{
				string number = null;
				if (!this.doc.TryGetRecordAttribute(DepartmentsRecordPath, i, "number", out number))
				{
					continue;
				}

				string name = null;
				if (!this.doc.TryGetRecordValue(DepartmentsRecordPath, i, out name))
				{
					name = string.Empty;
				}

				result.Add(new KeyValuePair<string, string>(number, name));
			}

			return result;
		}

		// Case-insensitive id lookup inside Defaults/Default (returns the
		// record index, or -1). Operator spelling is matched against the
		// file's ids; every other spelling in the file stays untouched.
		private int FindKeyIndex(string key)
		{
			StringComparer comparer = StringComparer.OrdinalIgnoreCase;
			int count = this.doc.Count(DefaultsRecordPath);

			for (int i = 0; i < count; i++)
			{
				string id = null;
				if (!this.doc.TryGetRecordAttribute(DefaultsRecordPath, i, "id", out id))
				{
					continue;
				}

				if (comparer.Equals(id, key))
				{
					return i;
				}
			}

			return -1;
		}

		// Atomic save (Decision 34: "every successful CONFIG write is an
		// immediate atomic write to Settings.xml"). Xml2 does the
		// deterministic render (ToXml: UTF-8, CRLF, two-space indent,
		// original order); we add the crash-safety envelope:
		//   1. render to memory          (no partial file risk yet)
		//   2. write to a sibling temp file
		//   3. overwrite-copy onto the real path (single replace op)
		//   4. delete the temp
		// A crash anywhere leaves the old complete file or the new
		// complete file - never a truncated settings.xml.
		public void Save()
		{
			string xml = this.doc.ToXml();
			string tempPath = this.path + ".tmp-" + Process.GetCurrentProcess().Id;

			try
			{
				File.WriteAllText(tempPath, xml, new UTF8Encoding(false));
				File.Copy(tempPath, this.path, true);
			}
			finally
			{
				// Never leave a temp behind, even on failure.
				try
				{
					if (File.Exists(tempPath))
					{
						File.Delete(tempPath);
					}
				}
				catch (IOException)
				{
					// best effort cleanup - the copy already failed
					// loudly and this is the secondary failure
				}
			}
		}

		// Decision 28: a corrupt settings file is named aside under a
		// dated backup name and a fresh copy lands in its place. The
		// rename itself is dumb file plumbing; the policy (when to do
		// it) lives in Program.cs startup.
		public static string RenameCorruptAside(string path)
		{
			string stamp = DateTime.Now.ToString("yyyy-MM-dd");
			string target = path + ".corrupt-" + stamp;
			File.Move(path, target);
			return target;
		}

		// Decision 31: XML engine errors carry their own prefix,
		// verbatim, with the element path and line Xml2 provides.
		// Format:  Settings Error: <message>(element <path>, line <n>)
		// - path/line parts appear only when the exception carries them.
		public static string FormatXmlError(Xml2.XmlSettingsException error)
		{
			string message = "Settings Error: " + error.Message;

			string elem = error.ElementPath;
			if (elem.Length > 0)
			{
				message = message + " (element " + elem + ", line " + error.Line + ")";
			}

			return message;
		}
	}
}
