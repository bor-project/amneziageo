"""Где: menu/routing
Что видно: новый список: выбор шаблона, блокировка рекламы
Состояние: любое; список не создаётся
"""


def run(ui):
    ui.open_section("Main_RailRoutingTitle")
    ui.click(key="Main_AddButton", wait=2)
    ui.find(key="Main_TemplateTitle")
    ui.save("routing-new")
