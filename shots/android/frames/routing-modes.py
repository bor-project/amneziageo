"""Где: menu/routing
Что видно: новый список, открыт выбор режима VPN для приложений
Состояние: любое; список не создаётся
"""


def run(ui):
    ui.open_section("Main_RailRoutingTitle")
    ui.click(key="Main_AddButton", wait=2)
    ui.tap(ui.find(key="Main_VpnModeSelected", cls="AdaptiveComboBox"))
    ui.save("routing-modes")
