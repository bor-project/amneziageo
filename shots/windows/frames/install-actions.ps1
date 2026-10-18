# Где: windows/install
# Что видно: установщик поверх установленной версии: «Удалить», «Восстановить», «Обновить»
# Состояние: установщик той же версии лежит в Downloads пользователя; ничего не применяется.
#   Установщик до AG-415 говорит на языке Windows (на стенде по формату региона), с AG-415 на языке приложения;
#   прежний установщик на чужом языке кадр пропускает
if (-not (Test-InstallerLang)) { return }
Start-Installer
Save-Window (Get-InstallerWindow) 'install-actions'
Stop-Installer
