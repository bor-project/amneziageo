# Где: menu/general
# Что видно: экспорт: отмечены первая конфигурация и список маршрутизации с правилами
# Состояние: «Импорт / экспорт» в «Общих», «Экспорт…»; файл не сохраняется
Reset-Ui
Open-Section 'Main_RailGeneralTitle'
Scroll-NameTo (T 'Main_ImportExportSection') 300
Start-Sleep -Milliseconds 500
Click-Key 'Main_ExportButton' | Out-Null
Start-Sleep -Seconds 2
$boxes = @(Get-OfType 'CheckBox' | Where-Object { $_.Current.IsEnabled })
foreach ($i in 0, 2) {
    (Get-Pattern $boxes[$i] ([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Start-Sleep -Milliseconds 400
}
Start-Sleep -Milliseconds 800
Save-Masked 'general-export'
