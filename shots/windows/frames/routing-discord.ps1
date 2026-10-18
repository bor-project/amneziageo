# Где: windows/recipes/discord-udp
# Что видно: редактор списка из шаблона «Недоступные сайты» на записи geosite:discord
# Состояние: список из шаблона, ничего не меняется
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Open-ListEditor
Scroll-NameTo 'geosite:discord' 250
Start-Sleep -Milliseconds 600
'ag-discord-voice in list: ' + [bool](Find-Element 'geoip:ag-discord-voice')
Save-Masked 'routing-discord' -Pattern (Get-StandPattern)
