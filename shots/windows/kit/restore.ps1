# Возвращает стенд после съёмки: имя списка, системные тема и язык, прежний формат.
$state = Join-Path $Script:Base 'state.xml'
if (-not (Test-Path $state)) { 'nothing to restore'; return }
$saved = Import-Clixml $state
$Lang = $saved.Lang
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Open-ListEditor
$edits = Get-OfType 'Edit'
if ((Get-Value $edits[0]) -ne $saved.List) {
    Set-Value $edits[0] $saved.List
    Start-Sleep -Milliseconds 800
    $edits[1].SetFocus()
    Start-Sleep -Milliseconds 800
    Click-Key 'Main_SaveButton' 'Button' | Out-Null
    Start-Sleep -Seconds 2
}
Click-Id 'RailGeneral' | Out-Null
Start-Sleep -Milliseconds 1500
Select-ComboItem (Get-OfType 'ComboBox')[0] (T-All 'Theme_System')
Start-Sleep -Milliseconds 800
Select-ComboItem (Get-OfType 'ComboBox')[1] (T-All 'Lang_System')
Start-Sleep -Milliseconds 1500
Set-Culture $saved.Culture
Stop-Ui
Remove-Item $state -Force
"culture: $($saved.Culture)"
Invoke-Cli routing list
Restart-Tunnel
