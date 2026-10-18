# Где: menu/routing
# Что видно: «Дополнительно»: весь UDP через туннель, время жизни маршрута, DNS-запросы системы, экспорт списка
# Состояние: редактор списка прокручен до «Дополнительно», блок раскрыт
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Open-ListEditor
Scroll-NameTo (T 'Main_AdvancedTitle') 300
Start-Sleep -Milliseconds 500
Click-Key 'Main_AdvancedTitle' | Out-Null
Start-Sleep -Milliseconds 1500
Save-Masked 'routing-advanced'
