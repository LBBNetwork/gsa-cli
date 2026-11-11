using System;
using System.IO;
using Xml2;

namespace BcGen
{
	/// <summary>
	/// CODE_METHOD lookup for bcgen (D68): reads settings.xml next to the
	/// executable when present - the same file gsa-cli uses when both tools
	/// are deployed side by side. Read-only: bcgen never materializes or
	/// writes a settings file. Missing file or missing key falls back to
	/// upc-a (gsa-cli's GenerateByCodeMethod default); a corrupt file is a
	/// hard error - bcgen never guesses the operator's intent.
	/// </summary>
	public static class SettingsReader
	{
		/// <summary>
		/// Directory of the running executable (Assembly.GetEntryAssembly,
		/// same idiom as GsACli.SettingsBootstrap); falls back to the current
		/// directory when the entry assembly cannot be located.
		/// </summary>
		public static string ExeDirectory()
		{
			System.Reflection.Assembly entry = System.Reflection.Assembly.GetEntryAssembly();
			if (entry != null)
			{
				try
				{
					string dir = Path.GetDirectoryName(entry.Location);
					if (dir != null && dir.Length > 0)
						return dir;
				}
				catch (Exception)
				{
					// fall through to the current directory
				}
			}

			return Directory.GetCurrentDirectory();
		}

		/// <summary>
		/// The default generation method (D70): "rnd4" or "upc-a-nc" when
		/// settings.xml's CODE_METHOD says so (case-insensitive, D12 key
		/// matching), otherwise "upc-a". Throws XmlSettingsException on a corrupt
		/// file - the caller formats it as `bcgen: Settings Error: ...`.
		/// </summary>
		public static string CodeMethod()
		{
			string dir = ExeDirectory();
			if (dir == null || dir.Length == 0)
				return "upc-a";

			string path = Path.Combine(dir, "settings.xml");
			if (!File.Exists(path))
				return "upc-a";   // no local settings: gsa-cli's default method

			XmlSettings doc = XmlSettings.Load(path);   // corrupt -> XmlSettingsException

			// Same record path + id-attribute lookup as GsACli.Settings (D12:
			// operator-typed keys match case-insensitively).
			StringComparer comparer = StringComparer.OrdinalIgnoreCase;
			int count = doc.Count("Defaults/Default");
			for (int i = 0; i < count; i++)
			{
				string id;
				if (!doc.TryGetRecordAttribute("Defaults/Default", i, "id", out id))
					continue;

				if (comparer.Equals(id, "CODE_METHOD"))
				{
					string value;
					if (!doc.TryGetRecordValue("Defaults/Default", i, out value))
						return "upc-a";   // key present but empty: default method

					string m = value.ToLowerInvariant();
					if (m == "rnd4") return "rnd4";
					if (m == "upc-a-nc") return "upc-a-nc";   // D70: UPC-A without check digit
					return "upc-a";
				}
			}

			return "upc-a";   // no CODE_METHOD directive: gsa-cli's default method
		}
	}
}