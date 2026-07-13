# Samples Windows kernel pool (paged + nonpaged) every 5 s and prints the
# growth rate, for attributing the EtwD/Etwr pool leak during isolation runs
# (ZmkVolumeFader --diag-sink / --diag-no-draw / --diag-no-tray / --diag-no-volume).
#
# Usage:  powershell -File diag-pool-watch.ps1 [-Seconds 5]
# No admin required. Ctrl+C to stop.
param([int]$Seconds = 5)

$fmt = "{0,-8} {1,14} {2,14} {3,12} {4,12}"
Write-Host ($fmt -f "time", "paged MB", "nonpaged MB", "paged/s", "nonpaged/s")

$prevP = $null
$prevN = $null
while ($true) {
    $c = Get-Counter '\Memory\Pool Paged Bytes', '\Memory\Pool Nonpaged Bytes'
    $p = ($c.CounterSamples | Where-Object { $_.Path -like '*paged bytes' -and $_.Path -notlike '*nonpaged*' }).CookedValue
    $n = ($c.CounterSamples | Where-Object { $_.Path -like '*nonpaged bytes' }).CookedValue
    $dp = ""; $dn = ""
    if ($null -ne $prevP) {
        $dp = "{0:+0.00;-0.00;0} MB" -f (($p - $prevP) / 1MB / $Seconds)
        $dn = "{0:+0.00;-0.00;0} MB" -f (($n - $prevN) / 1MB / $Seconds)
    }
    Write-Host ($fmt -f (Get-Date -Format HH:mm:ss), ("{0:N0}" -f ($p / 1MB)), ("{0:N0}" -f ($n / 1MB)), $dp, $dn)
    $prevP = $p; $prevN = $n
    Start-Sleep -Seconds $Seconds
}
