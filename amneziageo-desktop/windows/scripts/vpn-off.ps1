$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot '..\AmneziaGeo.Windows.App\bin\Debug\net10.0\AmneziaGeo.Windows.App.exe'
function ageo { & $exe @args }

Write-Host "stopping proba ..." -ForegroundColor Cyan
ageo stop proba | Out-Null
ageo uninstall proba | Out-Null
Start-Sleep -Seconds 2

Write-Host ("egress now (direct): " + (& curl.exe -s ifconfig.me/ip)) -ForegroundColor Cyan
Write-Host "VPN DOWN (app restored DNS automatically)." -ForegroundColor Yellow
