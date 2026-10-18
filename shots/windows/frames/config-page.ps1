# Где: menu/configurations
# Что видно: страница конфигурации: имя, текст конфигурации и настройки транспорта
# Состояние: шестерёнка второй карточки каталога
Reset-Ui
Open-Section 'Main_RailConfigTitle'
Click-Id 'SettingsPart' 1 | Out-Null
Start-Sleep -Milliseconds 1500
Save-Masked 'config-page'
