# Где: menu/routing
# Что видно: карточки списков: имя, режим, число правил по ролям, метка «Все приложения через VPN»
# Состояние: список из шаблона «Недоступные сайты» в работе; список «Всё, кроме выбранного региона» на время кадра
Add-FullList | Out-Null
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Save-Masked 'routing-cards'
Stop-Ui
Remove-FullList
