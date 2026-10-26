# Где: menu/routing
# Что видно: открытый список «Всё, кроме выбранного»: метка режима у заголовка и пояснение «Через VPN идут все приложения»
# Состояние: список «Всё, кроме выбранного региона» на время кадра, вторая карточка
Add-FullList | Out-Null
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Open-ListEditor 1
Save-Masked 'routing-warning'
Stop-Ui
Remove-FullList
