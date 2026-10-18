# Где: menu/home, windows/first-config
# Что видно: главный экран, туннель подключён, под кнопкой отклик, скорость и потери
# Состояние: поднята конфигурация стенда, через туннель идёт трафик
Reset-Ui
Start-Load 25
Start-Sleep -Seconds 10
Save-Masked 'home-connected'
Stop-Load
