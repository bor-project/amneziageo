# Где: windows/recipes/discord-udp, menu/configurations
# Что видно: страница конфигурации, «Прокси» (WebSocket) включён, вход предлагает сервер
# Состояние: вторая конфигурация каталога, не поднятая; «Прокси» включается на время кадра и возвращается как было
Reset-Ui
Open-Section 'Main_RailConfigTitle'
Click-Id 'SettingsPart' 1 | Out-Null
Start-Sleep -Milliseconds 1500
$was = Set-Switch 'Main_ProxyWebSocketLabel' $true
Start-Sleep -Seconds 3
Scroll-NameTo (T 'Main_ProxyWebSocketLabel') 150
Start-Sleep -Milliseconds 600
Save-Masked 'config-websocket'
if (-not $was) { Set-Switch 'Main_ProxyWebSocketLabel' $false | Out-Null }
'websocket back: ' + [string](Get-SwitchFor 'Main_ProxyWebSocketLabel').Current.ToggleState
