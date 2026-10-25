"""Где: menu/routing, linux/first-config
Что видно: новый список: выбор шаблона, блокировка рекламы
Состояние: любое; список не создаётся
"""


def run(ui):
    ui.open_section("Main_RailRoutingTitle")
    ui.click(key="Main_AddButton", wait=1.5)
    ui.find(key="Main_TemplateTitle")
    ui.save("routing-new")
