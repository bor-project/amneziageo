# Где: menu/connections
# Что видно: подключения, вкладка туннеля: оповещения, восстановление после перезагрузки, переподключение, сервер обновлений, несколько VPN
# Состояние: вкладка по умолчанию; «Сервер обновлений» - «По умолчанию», выбран рукой (`settings set update-source origin`)
Reset-Ui
Open-Section 'Main_RailConnectionsTitle'
Start-Sleep -Milliseconds 500
Save-Masked 'connections-tunnel'
