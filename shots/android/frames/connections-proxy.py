"""Где: menu/connections, recipes/lan-proxy
Что видно: раздел «Подключения»: разрешить подключения, порты SOCKS5 и HTTP, клиенты, вход без пароля
Состояние: прокси как есть на стенде; «Сервер обновлений» - «По умолчанию», выбран рукой (`settings set update-source origin`)
"""


def run(ui):
    ui.open_section("Main_RailConnectionsTitle")
    ui.save("connections-proxy")
