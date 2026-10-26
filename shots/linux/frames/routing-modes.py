"""Где: menu/routing
Что видно: новый список, открыт выбор режима VPN для приложений
Состояние: любое; список не создаётся, выбор закрывается Esc
"""


def run(ui):
    ui.open_section("Main_RailRoutingTitle")
    ui.click(key="Main_AddButton", wait=1.5)
    ui.tap(ui.find(key="Main_VpnModeSelected").up("AdaptiveComboBox"), wait=1.5)
    ui.save("routing-modes")
    ui.key("Escape")
