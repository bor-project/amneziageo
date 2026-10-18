# Где: menu/diagnostics
# Что видно: активные подключения с поиском youtube: адрес, домен, путь и причина
# Состояние: через туннель идёт трафик youtube; таблица берёт снимок при выборе, поэтому выбирается заново до строк youtube; маска только по стенду
Reset-Ui
Start-Load 120
Start-Sleep -Seconds 20
Open-Section 'Main_RailLogsTitle'
for ($i = 0; $i -lt 8; $i++) {
    Select-ComboItem (Get-OfType 'ComboBox')[0] 'routes' | Out-Null
    Start-Sleep -Seconds 1
    Select-ComboItem (Get-OfType 'ComboBox')[0] 'active' | Out-Null
    Start-Sleep -Seconds 4
    Set-Value (Get-OfType 'Edit')[0] 'youtube'
    Start-Sleep -Seconds 3
    if (@(Get-OfType 'Text' | Where-Object { $_.Current.Name -match '^[1-9]\d* \S+ [1-9]\d*, ' }).Count) { break }
}
"tries: $($i + 1)"
Save-LineMasked 'diag-active'
Stop-Load
