@echo off
rem csvtest.bat - compile Tools\CsvTest.exe against bin\CSVToolkit.dll under
rem the v2.0 gate, then run it (default command: all).
rem   csvtest.bat                 -> CsvTest.exe all
rem   csvtest.bat stream big.csv 8 2
setlocal
if exist "%WINDIR%\Microsoft.NET\Framework64\v2.0.50727\csc.exe" (
    set "CSC=%WINDIR%\Microsoft.NET\Framework64\v2.0.50727\csc.exe"
) else (
    set "CSC=%WINDIR%\Microsoft.NET\Framework\v2.0.50727\csc.exe"
)
"%CSC%" /nologo /target:exe /out:bin\CsvTest.exe /reference:bin\CSVToolkit.dll Tools\CsvTest.cs || exit /b 1
if "%~1"=="" (
    bin\CsvTest.exe all
) else (
    bin\CsvTest.exe %*
)
