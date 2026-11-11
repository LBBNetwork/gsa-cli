// AssemblyInfo - gsa-cli assembly identity (D66).
//
// The single source of truth for the version number: Program.PrintBanner
// reports it on the startup banner ("gsa-cli version ..."), and Windows
// file properties show the metadata below. Bump here only - build.bat,
// build.sh and gsa-cli.csproj all compile this file in (the library
// AssemblyInfo.cs files stay excluded from the csproj single-assembly
// build; their attributes would collide).

using System;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("gsa-cli")]
[assembly: AssemblyDescription("Command line utility for EALITEMR.DAT item databases (IBM 4690 / GSA).")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("The Crusty Computer Club")]
[assembly: AssemblyProduct("gsa-cli")]
[assembly: AssemblyCopyright("Copyleft 2026 - The Crusty Computer Club, https://crusty.computer")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// No [assembly: CLSCompliant(true)] - the GsACli sources follow the
// EalItemr idiom of underscore-prefixed private fields, which would trip
// CS3008 under a formal CLS declaration (same choice as CSVToolkit).
[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]
