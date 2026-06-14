$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot '..\AmneziaGeo.Windows.App\bin\Debug\net10.0\AmneziaGeo.Windows.App.exe'
function ageo { & $exe @args }

Write-Host "removing test tunnels/configs ..." -ForegroundColor Cyan
foreach ($c in 'proba', 'proba2', 'm1', 'm2', 'test', 'test2') {
    ageo stop $c 2>$null | Out-Null
    ageo uninstall $c 2>$null | Out-Null
    ageo config-remove $c 2>$null | Out-Null
}

Write-Host "removing duplicate geo sources added during testing ..." -ForegroundColor Cyan
foreach ($s in 'geoip-4', 'geosite-5') { ageo remove-source $s 2>$null | Out-Null }

Write-Host "resetting Ethernet DNS to DHCP ..." -ForegroundColor Cyan
Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ResetServerAddresses -ErrorAction SilentlyContinue
Clear-DnsClientCache

Write-Host "`nremaining configs:" -ForegroundColor Cyan
ageo config-list
Write-Host "`nremaining geo sources:" -ForegroundColor Cyan
ageo list-sources
Write-Host "`ncleanup done (kept the 3 working geo sources + downloaded .dat)." -ForegroundColor Yellow
