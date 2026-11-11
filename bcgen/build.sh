#!/bin/sh
# bcgen build script (Mono / Linux / macOS) - Decision 68.
# Same strict gate as gsa-cli: mcs -sdk:2.0 enforces the C# 2 / .NET 2.0
# API surface. Output (all into bin/): Xml2.dll, CSVToolkit.dll, then
# bcgen.exe - libraries first, the application EXE built against them.
set -e
cd "$(dirname "$0")"

mkdir -p bin

# --- library: Xml2 (GSAToolkit-style XML settings files) ------------------

mcs -sdk:2.0 -target:library -warn:1 \
	-out:"bin/Xml2.dll" \
	../Xml2/AssemblyInfo.cs \
	../Xml2/SettingPaths.cs \
	../Xml2/XmlNames.cs \
	../Xml2/XmlSettings.cs \
	../Xml2/XmlSettingsException.cs \
	../Xml2/XmlSettingsSnapshot.cs \
	../Xml2/XmlTreeNode.cs \
	../Xml2/XmlTreeParser.cs \
	../Xml2/XmlTreeWriter.cs

# --- library: CSVToolkit (general-purpose streaming CSV, C3.CSVToolkit) ---

mcs -sdk:2.0 -target:library -warn:1 \
	-out:"bin/CSVToolkit.dll" \
	../CSVToolkit/AssemblyInfo.cs \
	../CSVToolkit/AsyncResult.cs \
	../CSVToolkit/Csv.cs \
	../CSVToolkit/CsvCore.cs \
	../CSVToolkit/CsvException.cs \
	../CSVToolkit/CsvReader.cs \
	../CSVToolkit/CsvRow.cs \
	../CSVToolkit/CsvWriter.cs

# --- main application: bcgen.exe ------------------------------------------

mcs -sdk:2.0 -target:exe -warn:1 \
	-out:"bin/bcgen.exe" \
	-r:"bin/Xml2.dll" \
	-r:"bin/CSVToolkit.dll" \
	-win32icon:"../Hopstarter-Sleek-Xp-Basic-Barcode.ico" \
	BcGen/AssemblyInfo.cs \
	BcGen/Program.cs \
	BcGen/Codes.cs \
	BcGen/SettingsReader.cs

# --- post-build: LARGEADDRESSAWARE patch (D54) -----------------------------
# Same one-byte PE patch as gsa-cli's build; EXE only - DLLs inherit the
# process address space. Idempotent + self-verifying.

mcs -sdk:2.0 -warn:1 \
	-target:exe \
	-out:"bin/SetLA.tmp.exe" \
	../Tools/SetLargeAddressAware.cs

mono bin/SetLA.tmp.exe bin/bcgen.exe

rm -f "bin/SetLA.tmp.exe"

echo "built bin/bcgen.exe (+ bin/Xml2.dll, bin/CSVToolkit.dll)"
