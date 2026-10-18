"""Где: menu/connections, recipes/lan-proxy
Что видно: раздел «Подключения»: разрешить подключения, порты SOCKS5 и HTTP, клиенты, вход без пароля
Состояние: прокси как есть на стенде
"""


def run(ui):
    ui.open_section("Main_RailConnectionsTitle")
    ui.save("connections-proxy")
