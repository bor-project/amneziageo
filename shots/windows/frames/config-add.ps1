# Где: menu/configurations, windows/first-config
# Что видно: меню «Добавить» в каталоге: загрузить файл, вставить, сканировать QR-код, создать вручную
# Состояние: меню открыто, снимок с всплывающим окном
Reset-Ui
Open-Section 'Main_RailConfigTitle'
Click-Key 'Main_AddButton' 'Text' | Out-Null
Start-Sleep -Milliseconds 1500
Save-Masked 'config-add' -WithPopups
