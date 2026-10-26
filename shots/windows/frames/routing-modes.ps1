# Где: menu/routing
# Что видно: новый список, раскрыт выбор режима VPN для приложений
# Состояние: «Добавить» в маршрутизации, список не сохраняется
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Click-Id 'AddButton' | Out-Null
Start-Sleep -Milliseconds 1500
$ec = Open-Combo (Get-OfType 'ComboBox')[0]
Save-Masked 'routing-modes' -WithPopups
$ec.Collapse()
