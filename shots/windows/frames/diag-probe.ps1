# Где: menu/diagnostics
# Что видно: проба www.youtube.com: путь через туннель по правилу, отклик, приём и отдача
# Состояние: проба запущена и дождалась ответа
Reset-Ui
Open-Section 'Main_RailLogsTitle'
Select-ComboItem (Get-OfType 'ComboBox')[0] 'probe' | Out-Null
for ($i = 0; $i -lt 30; $i++) {
    $edit = (Get-OfType 'Edit')[0]
    if ($edit.Current.IsEnabled) { break }
    Start-Sleep -Seconds 2
}
Set-Value $edit 'www.youtube.com'
Start-Sleep -Seconds 1
Click-Id 'ProbeRunButton' | Out-Null
Start-Sleep -Seconds 30
Save-LineMasked 'diag-probe'
