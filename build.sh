#!/bin/sh
# gsa-cli build script (Mono / Linux / macOS).
# Output (all into bin/): EalItemr.dll, Xml2.dll, CSVToolkit.dll,
# then gsa-cli.exe and bcgen.exe - the libraries first, and the two
# application EXEs built against them (bcgen = Decision 68).
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
	GsACli/AssemblyInfo.cs \
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

# --- companion tool: bcgen.exe (Decision 68) --------------------------------
# Reuses the libraries just built into bin/; ships next to gsa-cli.exe in
# the distribution. Barcode icon per owner ruling (2026-11-11).

mcs -sdk:2.0 -target:exe -warn:1 \
	-out:"bin/bcgen.exe" \
	-r:"bin/Xml2.dll" \
	-r:"bin/CSVToolkit.dll" \
	-win32icon:"Hopstarter-Sleek-Xp-Basic-Barcode.ico" \
	bcgen/BcGen/AssemblyInfo.cs \
	bcgen/BcGen/Program.cs \
	bcgen/BcGen/Codes.cs \
	bcgen/BcGen/SettingsReader.cs

# --- post-build: LARGEADDRESSAWARE patch (D54) -----------------------------
# Same one-byte PE patch as build.bat; EXE only - DLLs inherit the process
# address space. Idempotent + self-verifying.

mcs -sdk:2.0 -warn:1 \
	-target:exe \
	-out:"bin/SetLA.tmp.exe" \
	Tools/SetLargeAddressAware.cs

mono bin/SetLA.tmp.exe bin/gsa-cli.exe

mono bin/SetLA.tmp.exe bin/bcgen.exe

rm -f "bin/SetLA.tmp.exe"

echo "built bin/gsa-cli.exe + bin/bcgen.exe (+ bin/EalItemr.dll, bin/Xml2.dll, bin/CSVToolkit.dll)"
