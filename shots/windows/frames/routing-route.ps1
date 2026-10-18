# Где: windows/recipes/multiple-vpn, menu/routing
# Что видно: редактор списка в режиме нескольких VPN: раскрытая запись geosite:youtube, в ней «Маршрут» и «Запасной»
# Состояние: режим и второй сервер стенда (конфигурация Helsinki) поднимаются на время кадра и возвращаются; правила не меняются
$partner = 'Helsinki'
Enter-Fleet $partner
try {
    Reset-Ui
    Open-Section 'Main_RailRoutingTitle'
    Open-ListEditor
    Click-Name ([string][char]0x25C2) 'Button' 2 | Out-Null
    Start-Sleep -Milliseconds 1500
    Scroll-NameTo 'geosite:youtube' 110
    Start-Sleep -Milliseconds 800
    Save-Masked 'routing-route' -Pattern (Get-StandPattern)
}
finally {
    Exit-Fleet $partner
}
