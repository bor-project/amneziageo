# Готовит стенд к кадрам языка $Lang: формат, светлая тема, язык окна, имя списка из шаблона.
$info = Get-Language
$state = Join-Path $Script:Base 'state.xml'
$saved = if (Test-Path $state) { Import-Clixml $state } else { $null }
$culture = (Get-Culture).Name
Set-Culture $info.Culture
Reset-Ui
Open-Settings
Click-Id 'RailGeneral' | Out-Null
Start-Sleep -Milliseconds 1500
Select-ComboItem (Get-OfType 'ComboBox')[0] (T-All 'Theme_Light')
Start-Sleep -Milliseconds 800
Select-ComboItem (Get-OfType 'ComboBox')[1] $info.Native
Start-Sleep -Milliseconds 1500
Click-Key 'Main_RailRoutingTitle' | Out-Null
Start-Sleep -Milliseconds 1500
Open-ListEditor
$edits = Get-OfType 'Edit'
$name = Get-Value $edits[0]
if (-not $saved) { $saved = [pscustomobject]@{ Culture = $culture; List = $name; Lang = $Lang } }
$saved.Lang = $Lang
$saved | Export-Clixml $state
"stand before: culture $($saved.Culture), list '$($saved.List)'"
$want = T 'Preset_ClosedName'
if ($name -ne $want) {
    Set-Value $edits[0] $want
    Start-Sleep -Milliseconds 800
    $edits[1].SetFocus()
    Start-Sleep -Milliseconds 800
    Click-Key 'Main_SaveButton' 'Button' | Out-Null
    Start-Sleep -Seconds 2
}
Invoke-Cli routing list
Stop-Ui
Restart-Tunnel
