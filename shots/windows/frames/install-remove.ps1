# Где: windows/install
# Что видно: удаление: отметка «Удалить конфигурации и кеш»
# Состояние: «Удалить» в установщике поверх установленной версии; «Продолжить» не нажимается; язык установщика как в install-actions
if (-not (Test-InstallerLang)) { return }
Start-Installer
Click-Any (T-All 'InstallerUi_Remove') ($AE::FromHandle((Get-InstallerWindow))) | Out-Null
Start-Sleep -Seconds 1
Save-Window (Get-InstallerWindow) 'install-remove'
Stop-Installer
