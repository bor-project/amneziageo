param(
    [string]$Site = '2ip.ru',
    [string]$Conf = (Join-Path $PSScriptRoot '..\..\.claude\tunnel-template.conf')
)
$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot '..\AmneziaGeo.Windows.App\bin\Debug\net10.0\AmneziaGeo.Windows.App.exe'
function ageo { & $exe @args }

Write-Host "bringing up proba (geo-split, only $Site through VPN) ..." -ForegroundColor Cyan
ageo install proba "$Conf" | Out-Null
ageo set-geo proba on "domain:$Site" | Out-Null
ageo start proba | Out-Null
Start-Sleep -Seconds 6

ageo uapi-get proba | Select-String "last_handshake_time_sec"
Write-Host ""
Write-Host "GEO-SPLIT UP: only $Site routes through VPN, everything else direct." -ForegroundColor Yellow
Write-Host "Open https://$Site in the browser -> it should show the server IP." -ForegroundColor Yellow
Write-Host "NOTE: disable Chrome 'Use secure DNS' (DoH) first, otherwise domain interception is bypassed." -ForegroundColor Yellow
Write-Host ("stop: powershell -NoProfile -ExecutionPolicy Bypass -File " + (Join-Path $PSScriptRoot 'vpn-off.ps1')) -ForegroundColor Yellow
