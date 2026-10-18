# Где: windows/recipes/multiple-vpn, menu/home
# Что видно: главный экран в режиме нескольких VPN: блок «Серверы», два поднятых сервера и их места
# Состояние: режим и второй сервер стенда (конфигурация Helsinki, другой интерфейс сервера) поднимаются на время кадра,
#   после кадра режим возвращается как был и туннель переподключается
$partner = 'Helsinki'
Enter-Fleet $partner
try {
    Reset-Ui
    Start-Load 20
    Start-Sleep -Seconds 8
    Save-Masked 'home-servers'
}
finally {
    Stop-Load
    Exit-Fleet $partner
}
