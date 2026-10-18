# Где: windows/install
# Что видно: «Параметры установки»: гео-базы, запуск после установки, подключение, ярлыки, «Сбросить настройки»
# Состояние: «Обновить» поверх установленной версии открывает те же параметры, что и новая установка; «Продолжить» не нажимается; язык установщика как в install-actions
if (-not (Test-InstallerLang)) { return }
Start-Installer
Click-Any (T-All 'InstallerUi_Update') ($AE::FromHandle((Get-InstallerWindow))) | Out-Null
Start-Sleep -Seconds 1
Save-Window (Get-InstallerWindow) 'install-options'
Stop-Installer
