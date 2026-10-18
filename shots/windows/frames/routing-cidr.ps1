# Где: windows/recipes/remote-network
# Что видно: подсказка при вводе диапазона cidr:10.8.0.0/24 в редакторе списка
# Состояние: правило набрано, но не добавляется; адрес из примера рецепта не размывается, маска только по стенду
Reset-Ui
Open-Section 'Main_RailRoutingTitle'
Open-ListEditor
Set-Value (Find-Id 'RuleBox') 'cidr:10.8.0.0/24'
Start-Sleep -Milliseconds 1500
Save-Masked 'routing-cidr' -Pattern (Get-StandPattern)
