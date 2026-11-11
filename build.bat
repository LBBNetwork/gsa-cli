@echo off
rem gsa-cli build script (Windows).
rem Compiler preference: in-box .NET Framework v2.0 csc - the strictest gate,
rem enforcing the C# 2 / .NET 2.0 API surface this project must stay within.
rem Falls back to any csc on PATH if the framework compiler is absent.
rem Output (all into bin\): EalItemr.dll, Xml2.dll, CSVToolkit.dll,
rem then gsa-cli.exe and bcgen.exe - the libraries first, and the two
rem application EXEs built against them (bcgen = Decision 68).
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v2.0.50727\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v2.0.50727\csc.exe"
if not exist "%CSC%" set "CSC=csc"

if not exist bin mkdir bin

rem --- library: EalItemr (GSA keyed-file record format) ------------------

"%CSC%" /nologo /target:library /out:bin\EalItemr.dll ^
	EalItemr\EalItemr.cs
if errorlevel 1 goto :fail

rem --- library: Xml2 (GSAToolkit-style XML settings files) ---------------

"%CSC%" /nologo /target:library /out:bin\Xml2.dll ^
	Xml2\AssemblyInfo.cs ^
	Xml2\SettingPaths.cs ^
	Xml2\XmlNames.cs ^
	Xml2\XmlSettings.cs ^
	Xml2\XmlSettingsException.cs ^
	Xml2\XmlSettingsSnapshot.cs ^
	Xml2\XmlTreeNode.cs ^
	Xml2\XmlTreeParser.cs ^
	Xml2\XmlTreeWriter.cs
if errorlevel 1 goto :fail

rem --- library: CSVToolkit (general-purpose streaming CSV, C3.CSVToolkit) --

"%CSC%" /nologo /target:library /out:bin\CSVToolkit.dll ^
	CSVToolkit\AssemblyInfo.cs ^
	CSVToolkit\AsyncResult.cs ^
	CSVToolkit\Csv.cs ^
	CSVToolkit\CsvCore.cs ^
	CSVToolkit\CsvException.cs ^
	CSVToolkit\CsvReader.cs ^
	CSVToolkit\CsvRow.cs ^
	CSVToolkit\CsvWriter.cs
if errorlevel 1 goto :fail

rem --- main application: gsa-cli.exe --------------------------------------

"%CSC%" /nologo /target:exe /out:bin\gsa-cli.exe ^
	/reference:bin\EalItemr.dll ^
	/reference:bin\Xml2.dll ^
	/reference:bin\CSVToolkit.dll ^
	/resource:settings.xml ^
	/resource:template.csv ^
	/win32icon:Aha-Soft-Large-Business-Cash-register.ico ^
	GsACli\AssemblyInfo.cs ^
	GsACli\Program.cs ^
	GsACli\Abort.cs ^
	GsACli\Repl.cs ^
	GsACli\Session.cs ^
	GsACli\CommandLex.cs ^
	GsACli\LineEditor.cs ^
	GsACli\DatabaseLock.cs ^
	GsACli\InstanceGuard.cs ^
	GsACli\Settings.cs ^
	GsACli\SettingsBootstrap.cs ^
	GsACli\CsvTemplate.cs ^
	Bridge\IRecordEngine.cs ^
	Bridge\EalItemrEngine.cs ^
	Bridge\ICsvEngine.cs ^
	Bridge\CsvEngine.cs

if errorlevel 1 goto :fail

if not exist bin\gsa-cli.exe (
	echo build FAILED - bin\gsa-cli.exe was not produced
	exit /b 1
)

rem --- companion tool: bcgen.exe (Decision 68) -----------------------------
rem Reuses the libraries just built into bin\; ships next to gsa-cli.exe in
rem the distribution. Barcode icon per owner ruling (2026-11-11).

"%CSC%" /nologo /target:exe /out:bin\bcgen.exe ^
	/reference:bin\Xml2.dll ^
	/reference:bin\CSVToolkit.dll ^
	/win32icon:Hopstarter-Sleek-Xp-Basic-Barcode.ico ^
	bcgen\BcGen\AssemblyInfo.cs ^
	bcgen\BcGen\Program.cs ^
	bcgen\BcGen\Codes.cs ^
	bcgen\BcGen\SettingsReader.cs
if errorlevel 1 goto :fail

if not exist bin\bcgen.exe (
	echo build FAILED - bin\bcgen.exe was not produced
	exit /b 1
)

rem --- post-build: LARGEADDRESSAWARE patch (D54) ---------------------------
rem The v2.0 csc emits the EXE without the large-address bit, and no
rem corflags/editpe on this box can set it, so we compile a tiny utility
rem with the same compiler and let it flip the one PE byte. EXE only - DLLs
rem inherit the process address space. Idempotent + self-verifying.

"%CSC%" /nologo /target:exe /out:bin\SetLA.tmp.exe Tools\SetLargeAddressAware.cs
if errorlevel 1 goto :fail

bin\SetLA.tmp.exe bin\gsa-cli.exe
if errorlevel 1 goto :fail

bin\SetLA.tmp.exe bin\bcgen.exe
if errorlevel 1 goto :fail

del bin\SetLA.tmp.exe

echo built bin\gsa-cli.exe + bin\bcgen.exe (+ bin\EalItemr.dll, bin\Xml2.dll, bin\CSVToolkit.dll)
exit /b 0

:fail
echo build FAILED - see compiler output above
exit /b 1
