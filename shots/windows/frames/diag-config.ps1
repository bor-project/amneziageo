# Где: menu/diagnostics
# Что видно: текущая конфигурация: состояние, маршрутизация и транспорт
# Состояние: строки с ключами и адресами стенда размыты
Reset-Ui
Open-Section 'Main_RailLogsTitle'
Select-ComboItem (Get-OfType 'ComboBox')[0] 'config' | Out-Null
Start-Sleep -Seconds 4
Save-LineMasked 'diag-config'
