# testsuite.ps1 - Phase 6a black-box test harness driver (D63).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/testsuite.ps1            # run all fast tests
#   powershell ... -File Tools/testsuite.ps1 -IncludeSlow                              # include slow/big tests
#   powershell ... -File Tools/testsuite.ps1 -Record t04-where-wildcards               # re-record expect.out for one test
#   powershell ... -File Tools/testsuite.ps1 -Record                                   # re-record ALL (use with care)
#
# Protocol (see Tools/tests/README.md):
#   each test dir contains run.sh (+ input.txt and other fixtures). The driver
#   gives every run.sh these env vars: GSA_EXE, WS_ROOT, TESTDIR, SCRATCH.
#   run.sh writes the transcript under test to $SCRATCH/actual.out and the exit
#   code of the primary gsa-cli invocation to $SCRATCH/exit.code as "EXIT=<n>".
#   The driver normalizes (CRLF->LF; pid/process numbers -> N) and compares
#   against the test dir's expect.out byte-for-byte.

param(
    [switch]$IncludeSlow,
    [string[]]$Record = @()
)

$ErrorActionPreference = 'Stop'
$WS_ROOT = Split-Path -Parent $PSScriptRoot          # Tools/.. -> workspace root
$GSA_EXE = Join-Path $WS_ROOT 'bin/gsa-cli.exe'
if (-not (Test-Path $GSA_EXE)) { throw "gsa-cli not built: $GSA_EXE missing. Run build.bat first." }

function Normalize([string]$text, [string]$scratch) {
    if ($null -eq $text) { return '' }
    # PS5.1 Set-Content -Encoding UTF8 writes a BOM; bash output has none.
    $t = $text.TrimStart([char]0xFEFF)
    $t = $t.Replace("`r`n", "`n").Replace("`r", "`n")
    # lock-test pids are non-deterministic; normalize them (D63).
    $t = [regex]::Replace($t, 'pid \d+', 'pid N')
    $t = [regex]::Replace($t, 'process \d+', 'process N')
    # D28 corrupt-settings rename carries today's date; normalize it.
    $t = [regex]::Replace($t, '\.corrupt-\d{4}-\d{2}-\d{2}', '.corrupt-DATE')
    # scratch paths are machine-specific; normalize both slash forms to <scratch>.
    if ($null -ne $scratch -and $scratch.Length -gt 0) {
        $bs = $scratch.Replace('/', '\\')
        $t = $t.Replace($bs, '<scratch>').Replace($scratch, '<scratch>')
    }
    return $t
}

$testRoot = Join-Path $PSScriptRoot 'tests'
$scratchRoot = Join-Path $testRoot '.scratch'
if (Test-Path $scratchRoot) { Remove-Item $scratchRoot -Recurse -Force }
New-Item -ItemType Directory -Path $scratchRoot | Out-Null
# D40: settings.xml lives next to the program file, so tests that run
# bin/gsa-cli.exe directly share one instance of it. The suite assumes a
# default state (t02/t10 expectations), so reset it before running; first
# launch re-bootstraps it from the embedded template.
$sharedSettings = Join-Path (Split-Path $GSA_EXE) 'settings.xml'
if (Test-Path $sharedSettings) { Remove-Item $sharedSettings -Force }

$dirs = Get-ChildItem $testRoot -Directory | Where-Object { $_.Name -match '^t\d+' } | Sort-Object Name
$pass = 0; $fail = 0; $skip = 0
$fails = @()

foreach ($d in $dirs) {
    $name = $d.Name
    $slow = Test-Path (Join-Path $d.FullName 'SLOW')
    if ($slow -and -not $IncludeSlow) {
        Write-Host ("SKIP  " + $name + "  (slow; use -IncludeSlow)")
        $skip++
        continue
    }

    $scratch = Join-Path $scratchRoot $name
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $runSh = Join-Path $d.FullName 'run.sh'
    if (-not (Test-Path $runSh)) { Write-Host ("FAIL  " + $name + "  (no run.sh)"); $fail++; $fails += $name; continue }

    # Run the test's run.sh under bash with the protocol env vars.
    $env:GSA_EXE = $GSA_EXE
    $env:WS_ROOT = $WS_ROOT
    $env:TESTDIR = $d.FullName
    $env:SCRATCH = $scratch
    # EAP=Stop makes native stderr throw in PS5.1; relax it around the call so
    # run.sh's exit code is what decides pass/fail, not a pipeline exception.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & bash $runSh 2>&1 | Out-Null
    $bashExit = $LASTEXITCODE
    $ErrorActionPreference = $prevEap

    $actualFile = Join-Path $scratch 'actual.out'
    $exitFile   = Join-Path $scratch 'exit.code'
    if ($bashExit -ne 0 -or -not (Test-Path $actualFile)) {
        Write-Host ("FAIL  " + $name + "  (run.sh failed, bash exit=$bashExit)")
        $fail++; $fails += $name; continue
    }

    $actual = Normalize (Get-Content $actualFile -Raw) $scratch
    $expectFile = Join-Path $d.FullName 'expect.out'

    if ($Record.Count -gt 0 -and ($Record -contains '*' -or $Record -contains $name)) {
        [System.IO.File]::WriteAllText($expectFile, $actual, (New-Object System.Text.UTF8Encoding $false))
        Write-Host ("RECORDED " + $name)
        continue
    }

    if (-not (Test-Path $expectFile)) {
        Write-Host ("FAIL  " + $name + "  (no expect.out; run with -Record)")
        $fail++; $fails += $name; continue
    }
    $expected = Normalize (Get-Content $expectFile -Raw) $scratch

    if ($actual -ceq $expected) {
        Write-Host ("PASS  " + $name)
        $pass++
    } else {
        # first diverging line, for quick diagnosis
        $aLines = $actual.Split("`n"); $eLines = $expected.Split("`n")
        $n = [Math]::Min($aLines.Count, $eLines.Count)
        $diffAt = -1
        for ($i = 0; $i -lt $n; $i++) { if ($aLines[$i] -cne $eLines[$i]) { $diffAt = $i + 1; break } }
        if ($diffAt -eq -1) { $diffAt = [Math]::Max($aLines.Count, $eLines.Count); Write-Host "      (line count differs: actual=$($aLines.Count) expected=$($eLines.Count))" }
        else {
            Write-Host ("      first diff at line " + $diffAt)
            if ($diffAt -le $eLines.Count) { Write-Host ("      expected: " + $eLines[$diffAt - 1]) }
            if ($diffAt -le $aLines.Count) { Write-Host ("      actual:   " + $aLines[$diffAt - 1]) }
        }
        Write-Host ("FAIL  " + $name)
        $fail++; $fails += $name
    }
}

Write-Host ""
Write-Host ("==== testsuite: pass=$pass fail=$fail skip=$skip")
if ($fail -gt 0) { Write-Host ("failed: " + ($fails -join ', ')) }
exit $fail
