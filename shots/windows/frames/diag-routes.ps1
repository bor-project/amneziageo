# Где: menu/diagnostics
# Что видно: журнал маршрутов с поиском youtube: разрешённые адреса и решение по правилу
# Состояние: через туннель идёт трафик youtube; маска только по стенду
Reset-Ui
Open-Section 'Main_RailLogsTitle'
Start-Load 40
Select-ComboItem (Get-OfType 'ComboBox')[0] 'routes' | Out-Null
Start-Sleep -Seconds 2
Set-Value (Get-OfType 'Edit')[0] 'youtube'
Start-Sleep -Seconds 6
Save-LineMasked 'diag-routes'
Stop-Load
