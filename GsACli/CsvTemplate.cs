using System;
using System.IO;
using System.Reflection;

namespace GsACli
{
	/// <summary>
	/// Blank CSV template delivery (D59): template.csv ships inside the binary as
	/// an embedded resource, and CREATE CSV copies it out to disk verbatim. The
	/// schema lives only in the resource file - if the CSV schema ever changes,
	/// swap the file and rebuild; no code change is needed (same trick as
	/// settings.xml / D40).
	/// </summary>
	public static class CsvTemplate
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
		/// Pull the template.csv resource out of this assembly's manifest resources
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
				if (!name.EndsWith("template.csv", StringComparison.OrdinalIgnoreCase))
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
		/// Copy the embedded blank CSV template out to targetPath, byte-for-byte.
		/// Returns the number of bytes written. Throws InvalidOperationException
		/// when the resource is missing from the image; IO failures propagate.
		/// </summary>
		public static long CreateBlankCsv(string targetPath)
		{
			byte[] template = ReadTemplateResource();
			if (template == null)
				throw new InvalidOperationException("embedded CSV template missing from gsa-cli image");

			File.WriteAllBytes(targetPath, template);
			return template.Length;
		}
	}
}
