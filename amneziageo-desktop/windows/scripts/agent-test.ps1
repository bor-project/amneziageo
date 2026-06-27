param(
    [string]$Target = 'proba',
    [string]$Conf = (Join-Path $PSScriptRoot '..\..\.claude\tunnel-template.conf'),
    [switch]$Stop
)
$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot '..\AmneziaGeo.Windows.App\bin\Debug\net10.0\AmneziaGeo.Windows.App.exe'
$log = Join-Path $env:ProgramData 'AmneziaGeo\logs\agent.log'
function ageo { & $exe @args }

if ($Stop) {
    Write-Host "stopping + removing the agent ..." -ForegroundColor Cyan
    ageo agent-stop | Out-Null
    ageo agent-uninstall | Out-Null
    Write-Host "agent removed; it deletes its member tunnel service on stop." -ForegroundColor Yellow
    Write-Host "run cleanup.ps1 to drop the test config." -ForegroundColor Yellow
    return
}

Write-Host ("egress BEFORE (direct): " + (& curl.exe -s ifconfig.me/ip)) -ForegroundColor Cyan

Write-Host "registering member config '$Target' (full tunnel) ..." -ForegroundColor Cyan
ageo config-add $Target "$Conf" | Out-Null
ageo set-geo $Target off | Out-Null

Write-Host "installing + starting the agent (single config = group of 1) ..." -ForegroundColor Cyan
ageo agent-install $Target | Out-Null
ageo agent-start | Out-Null
Start-Sleep -Seconds 10

Write-Host "`nagent service:" -ForegroundColor Cyan
ageo agent-status | Select-String "STATE"
Write-Host "member tunnel service (created by the agent):" -ForegroundColor Cyan
ageo config-list
Write-Host "`nagent log:" -ForegroundColor Cyan
if (Test-Path $log) { Get-Content $log -Tail 15 } else { Write-Host "(no log yet)" }

Write-Host ("`negress AFTER (through VPN): " + (& curl.exe -s ifconfig.me/ip)) -ForegroundColor Green
Write-Host ""
Write-Host "Agent runs as an auto-start SYSTEM service: survives UI close, logoff, reboot." -ForegroundColor Yellow
Write-Host ("stop+remove: powershell -NoProfile -ExecutionPolicy Bypass -File " + (Join-Path $PSScriptRoot 'agent-test.ps1') + " -Stop") -ForegroundColor Yellow
