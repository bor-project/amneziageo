# Где: menu/configurations
# Что видно: каталог конфигураций, карточки с откликом, скоростью и метками
# Состояние: две конфигурации стенда, одна поднята и несёт трафик
Reset-Ui
Open-Section 'Main_RailConfigTitle'
Start-Load 20
Start-Sleep -Seconds 8
Save-Masked 'config-catalog'
Stop-Load
