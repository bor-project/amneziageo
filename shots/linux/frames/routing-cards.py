"""Где: menu/routing
Что видно: карточка списка маршрутизации: имя, режим, число правил, метки, переключатель «Используется»
Состояние: список из шаблона «Недоступные сайты» выбран для кадров
"""


def run(ui):
    ui.open_section("Main_RailRoutingTitle")
    ui.find(cls="TextBlock", label=ui.status_list_name())
    ui.save("routing-cards")
