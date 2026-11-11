# memtest.ps1 - launch gsa-cli against a database with a fixed command
# script on stdin, poll the process working set every 50 ms, and report
# the peak. Used for the Phase 5f A/B memory measurement (D55).
param(
	[Parameter(Mandatory=$true)][string]$Exe,
	[Parameter(Mandatory=$true)][string]$Database,
	[Parameter(Mandatory=$true)][string]$InFile,
	[string]$Tag = "run"
)

$out = Join-Path $env:TEMP ("gsa-mem-out-" + $Tag + ".txt")
$p = Start-Process -FilePath $Exe `
	-ArgumentList "`"$Database`"" `
	-RedirectStandardInput $InFile `
	-RedirectStandardOutput $out `
	-PassThru

$max = 0.0
while (-not $p.HasExited) {
	try {
		$ws = [double](Get-Process -Id $p.Id).WorkingSet64 / 1MB
		if ($ws -gt $max) { $max = $ws }
	} catch {}
	Start-Sleep -Milliseconds 50
}

Write-Output ("TAG=" + $Tag + " MAX_WS_MB=" + [math]::Round($max, 1))
Get-Content $out | Select-Object -Last 4
