param([string]$Conf = (Join-Path $PSScriptRoot '..\..\.claude\tunnel-template.conf'))
$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot '..\AmneziaGeo.Windows.App\bin\Debug\net10.0\AmneziaGeo.Windows.App.exe'
function ageo { & $exe @args }

Write-Host ("egress BEFORE (direct): " + (& curl.exe -s ifconfig.me/ip)) -ForegroundColor Cyan

Write-Host "bringing up proba (full tunnel) ..." -ForegroundColor Cyan
ageo install proba "$Conf" | Out-Null
ageo set-geo proba off | Out-Null
ageo start proba | Out-Null
Start-Sleep -Seconds 7

Write-Host "handshake / bytes:" -ForegroundColor Cyan
ageo uapi-get proba | Select-String "last_handshake_time_sec|tx_bytes|rx_bytes"
Start-Sleep -Seconds 2

Write-Host ("egress AFTER (through VPN): " + (& curl.exe -s ifconfig.me/ip)) -ForegroundColor Green
Write-Host ""
Write-Host "VPN UP (full tunnel). DNS set by the app - open ANY site in the browser." -ForegroundColor Yellow
Write-Host ("stop: powershell -NoProfile -ExecutionPolicy Bypass -File " + (Join-Path $PSScriptRoot 'vpn-off.ps1')) -ForegroundColor Yellow
