# Где: menu/routing, windows/first-config
# Что видно: новый список, раскрыт выбор шаблона: готовый шаблон, «Вручную», «Импорт»
# Состояние: «Добавить» в маршрутизации, список не сохраняется
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Click-Id 'AddButton' | Out-Null
Start-Sleep -Milliseconds 1500
$ec = Open-Combo (Get-OfType 'ComboBox')[0]
Save-Masked 'routing-new' -WithPopups
$ec.Collapse()
