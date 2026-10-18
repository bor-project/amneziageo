# Где: menu/connections, windows/recipes/lan-proxy
# Что видно: вкладка прокси с разрешёнными подключениями: адреса, порты, клиенты и учётные записи
# Состояние: «Разрешить подключения» включается на время кадра и возвращается как было
Reset-Ui
Open-Section 'Main_RailConnectionsTitle'
Select-ComboItem (Get-OfType 'ComboBox')[0] (T 'General_ShareTabProxy') | Out-Null
Start-Sleep -Milliseconds 1500
$switch = @(Get-All | Where-Object { $_.Current.ClassName -eq 'ToggleSwitch' })[0]
$allow = Get-Pattern $switch ([System.Windows.Automation.TogglePattern]::Pattern)
$was = [string]$allow.Current.ToggleState
if ($was -ne 'On') { $allow.Toggle(); Start-Sleep -Seconds 3 }
Save-Masked 'connections-proxy'
if ($was -ne 'On') { $allow.Toggle(); Start-Sleep -Seconds 2 }
'allow: ' + $allow.Current.ToggleState
