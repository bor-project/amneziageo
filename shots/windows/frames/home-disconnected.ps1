# Где: menu/home
# Что видно: главный экран, туннель отключён, выбраны сервер и список маршрутизации
# Состояние: туннель опускается на время кадра и поднимается обратно
$selected = Get-Selected
Invoke-Cli down | Out-Null
Start-Sleep -Seconds 5
Reset-Ui
Save-Masked 'home-disconnected'
Invoke-Cli up $selected | Out-Null
Start-Sleep -Seconds 10
@(Invoke-Cli status) -match '^(state|tunnel|selected|routing)\s'
