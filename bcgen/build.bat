@echo off
rem bcgen build script (Windows) - Decision 68.
rem Same strict gate as gsa-cli: in-box .NET Framework v2.0 csc first, any
rem csc on PATH as fallback. Output (all into bin\): Xml2.dll, CSVToolkit.dll,
rem then bcgen.exe - libraries first, the application EXE built against them.
rem Self-contained deploy folder: bin\bcgen.exe + bin\Xml2.dll + bin\CSVToolkit.dll.
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v2.0.50727\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v2.0.50727\csc.exe"
if not exist "%CSC%" set "CSC=csc"

if not exist bin mkdir bin

rem --- library: Xml2 (GSAToolkit-style XML settings files) ---------------

"%CSC%" /nologo /target:library /out:bin\Xml2.dll ^
	..\Xml2\AssemblyInfo.cs ^
	..\Xml2\SettingPaths.cs ^
	..\Xml2\XmlNames.cs ^
	..\Xml2\XmlSettings.cs ^
	..\Xml2\XmlSettingsException.cs ^
	..\Xml2\XmlSettingsSnapshot.cs ^
	..\Xml2\XmlTreeNode.cs ^
	..\Xml2\XmlTreeParser.cs ^
	..\Xml2\XmlTreeWriter.cs
if errorlevel 1 goto :fail

rem --- library: CSVToolkit (general-purpose streaming CSV, C3.CSVToolkit) --

"%CSC%" /nologo /target:library /out:bin\CSVToolkit.dll ^
	..\CSVToolkit\AssemblyInfo.cs ^
	..\CSVToolkit\AsyncResult.cs ^
	..\CSVToolkit\Csv.cs ^
	..\CSVToolkit\CsvCore.cs ^
	..\CSVToolkit\CsvException.cs ^
	..\CSVToolkit\CsvReader.cs ^
	..\CSVToolkit\CsvRow.cs ^
	..\CSVToolkit\CsvWriter.cs
if errorlevel 1 goto :fail

rem --- main application: bcgen.exe ----------------------------------------

"%CSC%" /nologo /target:exe /out:bin\bcgen.exe ^
	/reference:bin\Xml2.dll ^
	/reference:bin\CSVToolkit.dll ^
	/win32icon:..\Hopstarter-Sleek-Xp-Basic-Barcode.ico ^
	BcGen\AssemblyInfo.cs ^
	BcGen\Program.cs ^
	BcGen\Codes.cs ^
	BcGen\SettingsReader.cs
if errorlevel 1 goto :fail

if not exist bin\bcgen.exe (
	echo build FAILED - bin\bcgen.exe was not produced
	exit /b 1
)

rem --- post-build: LARGEADDRESSAWARE patch (D54) ---------------------------
rem Same convention as gsa-cli's build: the v2.0 csc emits the EXE without
rem the large-address bit, so a tiny utility compiled with the same compiler
rem flips the one PE byte. Idempotent + self-verifying.

"%CSC%" /nologo /target:exe /out:bin\SetLA.tmp.exe ..\Tools\SetLargeAddressAware.cs
if errorlevel 1 goto :fail

bin\SetLA.tmp.exe bin\bcgen.exe
if errorlevel 1 goto :fail

del bin\SetLA.tmp.exe

echo built bin\bcgen.exe (+ bin\Xml2.dll, bin\CSVToolkit.dll)
exit /b 0

:fail
echo build FAILED - see compiler output above
exit /b 1
