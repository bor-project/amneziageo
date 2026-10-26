"""Где: menu/routing, linux/first-config
Что видно: новый список: режим VPN для приложений, выбор шаблона, блокировка рекламы
Состояние: любое; список не создаётся
"""


def run(ui):
    ui.open_section("Main_RailRoutingTitle")
    ui.click(key="Main_AddButton", wait=1.5)
    ui.find(key="Main_VpnModeSelected")
    ui.save("routing-new")
