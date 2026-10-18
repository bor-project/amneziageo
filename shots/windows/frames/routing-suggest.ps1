# Где: menu/routing
# Что видно: подсказки при вводе netflix: точный домен, IP-диапазоны и домены категории
# Состояние: в поле адреса редактора набрано netflix, правило не добавляется
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Open-ListEditor
Set-Value (Find-Id 'RuleBox') 'netflix'
Start-Sleep -Milliseconds 1500
Save-Masked 'routing-suggest'
