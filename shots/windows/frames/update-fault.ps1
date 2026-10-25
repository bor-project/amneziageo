# Где: menu/home, «Ошибка проверки обновлений»
# Что видно: главный экран, знак у состояния и окно с причиной и «Скрыть»
# Состояние: как у update-source: выбор рукой снят на время кадра, проверка не удалась, сервер не отвечает; знак не скрывается
$was = ((@(Invoke-Cli settings show) -match '^update-source\s') -replace '^update-source\s+', '').Trim()
Invoke-Cli settings set update-source auto | Out-Null
Invoke-Cli update check | Out-Null
Start-Sleep -Seconds 5
Reset-Ui
Wait-Until { @(Get-All | Where-Object { $_.Current.AutomationId -eq 'MarkPart' }).Count -gt 0 } 60
$mark = @(Get-All | Where-Object { $_.Current.AutomationId -eq 'MarkPart' })[-1]
Invoke-Element $mark | Out-Null
Start-Sleep -Milliseconds 1200
Save-Masked 'update-fault' -WithPopups
if ($was -eq 'origin') { Invoke-Cli settings set update-source origin | Out-Null }
@(Invoke-Cli status) -match '^update (source|error)\s'
