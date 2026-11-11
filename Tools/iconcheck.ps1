# iconcheck.ps1 - verify a Win32 .ico (RT_GROUP_ICON, type 14) is embedded in a PE file.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File Tools/iconcheck.ps1 <path-to-exe-or-dll>
# Loads the image as a resource-only mapping (never executes it), enumerates
# resource types via kernel32, and reports which types are present plus how
# many names sit under RT_GROUP_ICON.

param(
	[Parameter(Mandatory = $true)][string]$Path
)

$code = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class IconCheck
{
	public delegate bool EnumResProc(IntPtr hModule, IntPtr lptype, IntPtr lpszName, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

	[DllImport("kernel32.dll")]
	public static extern bool FreeLibrary(IntPtr hModule);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	public static extern bool EnumResourceTypes(IntPtr hModule, EnumResProc lpEnumFunc, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	public static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResProc lpEnumFunc, IntPtr lParam);

	private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

	public static List<int> FoundTypes = new List<int>();
	public static int GroupIconNames = 0;

	public static string Run(string path)
	{
		FoundTypes.Clear();
		GroupIconNames = 0;
		IntPtr module = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_IMAGE_RESOURCE);
		if (module == IntPtr.Zero) return "LOAD FAILED";
		try
		{
			EnumResourceTypes(module, new EnumResProc(OnType), IntPtr.Zero);
		}
		finally
		{
			FreeLibrary(module);
		}
		string joined = "";
		for (int i = 0; i < FoundTypes.Count; i++)
		{
			if (i > 0) joined += ",";
			joined += FoundTypes[i].ToString();
		}
		return "types=" + joined + " groupIconNames=" + GroupIconNames.ToString();
	}

	public static bool OnType(IntPtr hModule, IntPtr lptype, IntPtr lpszName, IntPtr lParam)
	{
		int t = lptype.ToInt32();
		FoundTypes.Add(t);
		if (t == 14) // RT_GROUP_ICON: count the icon names under it
		{
			EnumResourceNames(hModule, lptype, new EnumResProc(OnName), IntPtr.Zero);
		}
		return true;
	}

	public static bool OnName(IntPtr hModule, IntPtr lptype, IntPtr lpszName, IntPtr lParam)
	{
		GroupIconNames++;
		return true;
	}
}
"@

Add-Type -TypeDefinition $code
$result = [IconCheck]::Run($Path)
Write-Host ("ICONCHECK " + (Split-Path $Path -Leaf) + ": " + $result)

if ($result -match "groupIconNames=([1-9])") { Write-Host "RESULT: icon embedded"; exit 0 }
Write-Host "RESULT: no RT_GROUP_ICON resource found"
exit 1
