using System.Reflection;

// bcgen identity + version stamp (D68). Same convention as GsACli's own
// AssemblyInfo.cs (D66): the attribute file is the single source of truth,
// so no build path carries a /version: flag and they cannot drift.
[assembly: AssemblyTitle("bcgen")]
[assembly: AssemblyDescription("CSV item-code pre-processor for gsa-cli imports (Decision 68)")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("The Crusty Computer Club")]
[assembly: AssemblyProduct("gsa-cli tools")]
[assembly: AssemblyCopyright("Copyleft 2026 - The Crusty Computer Club, https://crusty.computer")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
