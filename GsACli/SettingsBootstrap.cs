using System;
using System.IO;
using System.Reflection;

namespace GsACli
{
	/// <summary>
	/// Settings template delivery (D40): settings.xml ships inside the binary as
	/// an embedded resource; at startup it is copied out next to the executable
	/// when no local copy exists. Existing files are never touched here — the
	/// missing/corrupt matrix lives in Program.BootSettings (D28/29/40).
	/// </summary>
	public static class SettingsBootstrap
	{
		/// <summary>
		/// Read a stream fully into a byte array (.NET 2.0 has no Stream extension for this).
		/// </summary>
		static byte[] ReadAllBytes(Stream stream)
		{
			MemoryStream buffer = new MemoryStream();
			byte[] chunk = new byte[8192];
			int read;
			while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
				buffer.Write(chunk, 0, read);
			return buffer.ToArray();
		}

		/// <summary>
		/// Pull the settings.xml template out of this assembly's manifest resources
		/// (matching by suffix so the build-tool resource naming is irrelevant).
		/// Returns null when no such resource is present.
		/// </summary>
		static byte[] ReadTemplateResource()
		{
			Assembly entry = Assembly.GetEntryAssembly();
			if (entry == null)
				return null;

			foreach (string name in entry.GetManifestResourceNames())
			{
				if (!name.EndsWith("settings.xml", StringComparison.OrdinalIgnoreCase))
					continue;

				Stream resource = entry.GetManifestResourceStream(name);
				if (resource == null)
					return null;

				using (resource)
					return ReadAllBytes(resource);
			}

			return null;
		}

		/// <summary>
		/// Copy the embedded settings.xml template out beside the executable when no
		/// local copy exists (D40). Returns the settings file path, or null when
		/// the template is missing from the image and no local file exists.
		/// Never overwrites an existing file (D40's first-copy rule).
		/// </summary>
		public static string EnsureLocalTemplate()
		{
			string exeDir = null;
			Assembly entry = Assembly.GetEntryAssembly();
			if (entry != null)
			{
				try
				{
					exeDir = Path.GetDirectoryName(entry.Location);
				}
				catch (Exception)
				{
					exeDir = null;
				}
			}
			if (exeDir == null || exeDir.Length == 0)
				exeDir = Directory.GetCurrentDirectory();

			string target = Path.Combine(exeDir, "settings.xml");
			if (File.Exists(target))
				return target;

			byte[] template = ReadTemplateResource();
			if (template == null)
			{
				Console.Error.WriteLine("Error: embedded settings template missing from gsa-cli image");
				return null;
			}

			File.WriteAllBytes(target, template);
			return target;
		}
	}
}
