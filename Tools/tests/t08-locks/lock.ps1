# t08 lock-semantics orchestrator (D14/D30 contract, pinned by D63).
# Runs in $env:SCRATCH where run.sh has copied the sample DB as t.DAT.
# Emits one concatenated transcript to stdout; pids are normalized by the driver.
$ErrorActionPreference = 'Stop'
Set-Location $env:SCRATCH
$exe = $env:GSA_EXE

function KillAll {
    Get-Process gsa-cli -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 1
}

# NB: the parameter must NOT be named $args (PowerShell automatic variable).
function New-Proci([string]$argText) {
    $i = New-Object System.Diagnostics.ProcessStartInfo
    $i.FileName = $exe
    if ($argText.Length -gt 0) { $i.Arguments = $argText }
    $i.RedirectStandardInput = $true
    $i.RedirectStandardOutput = $true
    $i.UseShellExecute = $false
    return $i
}

function Send-And-Capture([System.Diagnostics.ProcessStartInfo]$info, [string[]]$lines) {
    $p = [System.Diagnostics.Process]::Start($info)
    try {
        foreach ($l in $lines) { $p.StandardInput.WriteLine($l) }
        $p.StandardInput.Flush()
    } catch [System.IO.IOException] {
        # The process exited before reading stdin (e.g. S2's boot refusal);
        # the write is moot - its exit code and captured stdout are what count.
    }
    try { $p.StandardInput.Close() } catch {}
    $o = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit() | Out-Null
    return @{ Text = $o; Exit = $p.ExitCode }
}

KillAll
Remove-Item t.DAT.lock -ErrorAction SilentlyContinue

# --- S1: holder (stdin held open) loads the DB and owns the lock file -------
# Drain the holder's stdout on a background thread (BeginOutputReadLine) so its
# pipe can never fill and block it; we do not need its output in the transcript.
$holder = [System.Diagnostics.Process]::Start((New-Proci 't.DAT'))
$holder.BeginOutputReadLine() | Out-Null
$deadline = (Get-Date).AddSeconds(20)
while (-not (Test-Path t.DAT.lock)) {
    if ((Get-Date) -gt $deadline) { throw "S1: holder never acquired the lock" }
    Start-Sleep 0.5
}

# S1 second process: plain LOAD refused against a live holder, exit=1
$r1 = Send-And-Capture (New-Proci 't.DAT') @('BYE')
if ($r1.Exit -ne 1) { throw "S1: expected exit=1, got $($r1.Exit)" }
Write-Host $r1.Text

# --- S2: LOAD OVERRIDE refused against a live gsa-cli holder -----------------
$r2 = Send-And-Capture (New-Proci '') @('LOAD OVERRIDE t.DAT')
if ($r2.Text -notmatch 'override was refused') { throw "S2: expected override refusal" }
Write-Host $r2.Text

# --- kill the holder uncleanly -> stale lock file must remain ---------------
$holder.Kill() | Out-Null
$holder.WaitForExit() | Out-Null
Start-Sleep 1
if (-not (Test-Path t.DAT.lock)) { throw "S3: expected stale lock file to remain" }

# --- S3: plain LOAD vs stale lock -> refused, no auto-detection --------------
$r3 = Send-And-Capture (New-Proci '') @('LOAD t.DAT')
if ($r3.Text -notmatch 'already in use by process N|already in use by process \d+') { throw "S3: expected stale-lock refusal" }
Write-Host $r3.Text

# --- S4: LOAD OVERRIDE vs dead holder -> succeeds ----------------------------
$r4 = Send-And-Capture (New-Proci '') @('LOAD OVERRIDE t.DAT')
if ($r4.Text -notmatch 'Loaded t\.DAT OK') { throw "S4: expected successful override load" }
Write-Host $r4.Text

KillAll
Remove-Item t.DAT.lock -ErrorAction SilentlyContinue
