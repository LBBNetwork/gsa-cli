# Lock-contention test: hold a live session on t.DAT, then try a one-shot IMPORT.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path (Get-Location) 'bin\gsa-cli.exe'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)

$p.StandardInput.WriteLine("LOAD 't.DAT';")

# Wait until the holder reports Loaded (or give up after 15s).
$loaded = $false
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt 15000) {
    if (-not $p.StandardOutput.Peek()) { Start-Sleep -Milliseconds 100; continue }
    $line = $p.StandardOutput.ReadLine()
    if ($line -like '*Loaded*') { $loaded = $true; break }
}
if (-not $loaded) { Write-Host 'HOLDER FAILED TO LOAD'; $p.Kill(); exit 2 }
Write-Host 'holder: database loaded, lock held'

# Attempt the one-shot import against the locked file.
$imp = Start-Process -FilePath (Join-Path (Get-Location) 'bin\gsa-cli.exe') `
        -ArgumentList 't.DAT','-import','import5.csv' `
        -NoNewWindow -Wait -PassThru -RedirectStandardOutput 'locktest.imp.log'
Write-Host ('IMPORT EXIT=' + $imp.ExitCode)
Get-Content 'locktest.imp.log'

$p.StandardInput.Close()
Start-Sleep -Milliseconds 300
if (-not $p.HasExited) { $p.Kill() }
$p.WaitForExit() | Out-Null
exit 0
