# memproc.ps1 - launch any exe with arguments, poll its working set every
# 50 ms, report the peak. Generic sibling of memtest.ps1 (which is bound to
# gsa-cli's stdin protocol). Used for CSVToolkit streaming-bound checks.
param(
	[Parameter(Mandatory=$true)][string]$Exe,
	[string]$ArgsText = "",
	[string]$Tag = "run"
)

$out = Join-Path $env:TEMP ("memproc-out-" + $Tag + ".txt")
$p = Start-Process -FilePath $Exe `
	-ArgumentList $ArgsText `
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
Get-Content $out | Select-Object -Last 6
