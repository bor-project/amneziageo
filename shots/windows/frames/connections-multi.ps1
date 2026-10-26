# Где: windows/recipes/multiple-vpn, menu/connections
# Что видно: вкладка туннеля, «Разрешить несколько VPN» включён, под ним настройки перехода правил между серверами
# Состояние: переключатель включается на время кадра и возвращается как было; «Сервер обновлений» - «По умолчанию», выбран рукой
Reset-Ui
Open-Section 'Main_RailConnectionsTitle'
$was = Set-Switch 'General_MultiServer' $true
Start-Sleep -Seconds 2
Scroll-NameTo (T 'General_MultiServer') 120
Start-Sleep -Milliseconds 600
Save-Masked 'connections-multi'
if (-not $was) { Set-Switch 'General_MultiServer' $false | Out-Null }
'multi back: ' + [string](Get-SwitchFor 'General_MultiServer').Current.ToggleState
@(Invoke-Cli status) -match '^(state|tunnel|selected|routing)\s'
