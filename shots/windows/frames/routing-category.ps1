# Где: menu/routing
# Что видно: раскрытая запись geosite:youtube: число записей, диапазоны адресов и домены
# Состояние: в редакторе раскрыта третья запись списка; маска только по стенду, адреса диапазонов видны
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Open-ListEditor
Click-Name ([string][char]0x25C2) 'Button' 2 | Out-Null
Start-Sleep -Milliseconds 1500
Scroll-NameTo 'geosite:youtube' 110
Start-Sleep -Milliseconds 600
Save-Masked 'routing-category' -Pattern (Get-StandPattern)
