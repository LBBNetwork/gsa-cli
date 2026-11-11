using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("CSVToolkit")]
[assembly: AssemblyDescription("General-purpose streaming CSV reader/writer with a parallel, deterministic pipeline (C3.CSVToolkit).")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("CSVToolkit")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Note: no [assembly: CLSCompliant(true)] - this library follows the
// EalItemr idiom of underscore-prefixed private fields, which would trip
// CS3008 under a formal CLS declaration (EalItemr.cs makes the same choice;
// Xml2 declares CLS because it has no such fields). The public API surface
// is CLS-compliant in substance.
[assembly: ComVisible(false)]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

[assembly: InternalsVisibleTo("CSVToolkit.Tests")]
