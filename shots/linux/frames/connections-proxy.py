"""Где: menu/connections, linux/recipes/lan-proxy
Что видно: «Подключения», «Прокси»: разрешить подключения, порты SOCKS5 и HTTP, клиенты, вход без пароля
Состояние: прокси как есть на стенде
"""


def run(ui):
    ui.open_section("Main_RailConnectionsTitle")
    ui.pick(ui.find(key="General_ShareTabProxy").up("AdaptiveComboBox"), ui.T_all("General_ShareTabProxy"))
    ui.save("connections-proxy")
