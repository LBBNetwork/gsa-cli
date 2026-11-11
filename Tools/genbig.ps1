# genbig.ps1 - generate big.csv: header + N data rows, UTF-8 BOM, CRLF
# between rows, NO trailing newline after the last row (CSV-3 compliant).
param(
	[int]$Rows = 1000000,
	[string]$Out = "big.csv"
)

$enc = New-Object System.Text.UTF8Encoding($true)
$fs = [System.IO.File]::Create($Out)
try {
	$w = New-Object System.IO.StreamWriter($fs, $enc)
	$w.Write('ITEM_CODE,ITEM_NAME,DEPT,SALEPRICECENTS,SALEPRICE2,QTY,TRACK_MOVEMENT,REQUIRE_QTY,REQUIRE_PRICE,SELL_HOURS,SELL_RESTRICTED,NO_RETURN,GIVEAWAY,TAX1,TAX2,TAX3,TAX4,PRICE_METHOD,DCS_METHOD')
	for ($i = 0; $i -lt $Rows; $i++) {
		$w.Write("`r`n")
		$w.Write(('9' + $i) + ',BIG ' + $i + ',1,100,0,0,N,N,N,Y,Y,N,N,N,N,N,N,0,0')
	}
	$w.Flush()
} finally {
	$w.Close()
}
(Get-Item $Out).Length
