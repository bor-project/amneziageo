# Где: menu/connections, «Сервер обновлений»
# Что видно: вкладка туннеля, открытый список «Сервер обновлений»: «По умолчанию» с адресом GitHub под ним и адрес сервера конфигурации, под строкой причина
# Состояние: панель сервера конфигурации раздаёт обновления по адресу updates.example.com:8445 с «Включён по умолчанию» (адрес не отвечает); задание снимает выбор рукой на время кадра и возвращает «По умолчанию», если он был выбран
$was = ((@(Invoke-Cli settings show) -match '^update-source\s') -replace '^update-source\s+', '').Trim()
Invoke-Cli settings set update-source auto | Out-Null
Invoke-Cli update check | Out-Null
Start-Sleep -Seconds 5
Reset-Ui
Open-Section 'Main_RailConnectionsTitle'
Start-Sleep -Milliseconds 500
Scroll-NameTo (T 'Connections_UpdateSource') 200
Start-Sleep -Milliseconds 600
$ec = Open-Combo (Get-OfType 'ComboBox')[1]
Start-Sleep -Milliseconds 600
Save-Masked 'update-source' -WithPopups
$ec.Collapse()
if ($was -eq 'origin') { Invoke-Cli settings set update-source origin | Out-Null }
@(Invoke-Cli status) -match '^update (source|error)\s'
