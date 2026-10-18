# Где: menu/geo-sources
# Что видно: источники geo: автообновление, добавление базы и список источников
# Состояние: из редактора списка по ссылке «Источники geo»
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Open-ListEditor
Click-Key 'Main_SourcesSection' | Out-Null
Start-Sleep -Milliseconds 2000
Save-Masked 'geo-sources'
