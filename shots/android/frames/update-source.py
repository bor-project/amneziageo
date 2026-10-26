"""Где: menu/connections, «Сервер обновлений»
Что видно: открытый список сервера обновлений: «По умолчанию» с адресом GitHub под ним и адрес сервера конфигурации, под строкой причина
Состояние: панель сервера конфигурации раздаёт обновления по адресу updates.example.com:8445 с «Включён по умолчанию» (адрес не отвечает), выбор рукой снят; список не меняется
"""


def run(ui):
    ui.open_section("Main_RailConnectionsTitle")
    combo = ui.after(ui.find(key="Connections_UpdateSource"), cls="AdaptiveComboBox")
    ui.tap(combo, wait=2)
    ui.find(match="^" + ui.T("Connections_UpdateSourceDefault") + "$")
    ui.save("update-source")
    ui.back()
