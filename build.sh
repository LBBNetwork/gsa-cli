#!/bin/sh
# gsa-cli build script (Mono / Linux / macOS).
# Output (all into bin/): EalItemr.dll, Xml2.dll, CSVToolkit.dll,
# then gsa-cli.exe - the libraries first, and the main application
# EXE built against them.
# Change defaults by editing settings.xml (the embedded template) and rebuilding.
set -e
cd "$(dirname "$0")"

mkdir -p bin

# --- library: EalItemr (GSA keyed-file record format) ---------------------

mcs -sdk:2.0 -target:library -warn:1 \
	-out:"bin/EalItemr.dll" \
	EalItemr/EalItemr.cs

# --- library: Xml2 (GSAToolkit-style XML settings files) ------------------

mcs -sdk:2.0 -target:library -warn:1 \
	-out:"bin/Xml2.dll" \
	Xml2/AssemblyInfo.cs \
	Xml2/SettingPaths.cs \
	Xml2/XmlNames.cs \
	Xml2/XmlSettings.cs \
	Xml2/XmlSettingsException.cs \
	Xml2/XmlSettingsSnapshot.cs \
	Xml2/XmlTreeNode.cs \
	Xml2/XmlTreeParser.cs \
	Xml2/XmlTreeWriter.cs

# --- library: CSVToolkit (general-purpose streaming CSV, C3.CSVToolkit) ---

mcs -sdk:2.0 -target:library -warn:1 \
	-out:"bin/CSVToolkit.dll" \
	CSVToolkit/AssemblyInfo.cs \
	CSVToolkit/AsyncResult.cs \
	CSVToolkit/Csv.cs \
	CSVToolkit/CsvCore.cs \
	CSVToolkit/CsvException.cs \
	CSVToolkit/CsvReader.cs \
	CSVToolkit/CsvRow.cs \
	CSVToolkit/CsvWriter.cs

# --- main application: gsa-cli.exe ----------------------------------------

mcs -sdk:2.0 -target:exe -warn:1 \
	-out:"bin/gsa-cli.exe" \
	-r:"bin/EalItemr.dll" \
	-r:"bin/Xml2.dll" \
	-r:"bin/CSVToolkit.dll" \
	-resource:settings.xml \
	-resource:template.csv \
	-win32icon:"Aha-Soft-Large-Business-Cash-register.ico" \
	GsACli/Program.cs \
	GsACli/Abort.cs \
	GsACli/Repl.cs \
	GsACli/Session.cs \
	GsACli/CommandLex.cs \
	GsACli/LineEditor.cs \
	GsACli/DatabaseLock.cs \
	GsACli/InstanceGuard.cs \
	GsACli/Settings.cs \
	GsACli/SettingsBootstrap.cs \
	GsACli/CsvTemplate.cs \
	Bridge/IRecordEngine.cs \
	Bridge/EalItemrEngine.cs \
	Bridge/ICsvEngine.cs \
	Bridge/CsvEngine.cs

# --- post-build: LARGEADDRESSAWARE patch (D54) -----------------------------
# Same one-byte PE patch as build.bat; EXE only - DLLs inherit the process
# address space. Idempotent + self-verifying.

mcs -sdk:2.0 -warn:1 \
	-target:exe \
	-out:"bin/SetLA.tmp.exe" \
	Tools/SetLargeAddressAware.cs

mono bin/SetLA.tmp.exe bin/gsa-cli.exe

rm -f "bin/SetLA.tmp.exe"

echo "built bin/gsa-cli.exe (+ bin/EalItemr.dll, bin/Xml2.dll, bin/CSVToolkit.dll)"
