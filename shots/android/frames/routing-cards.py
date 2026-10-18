"""Где: menu/routing
Что видно: карточка списка маршрутизации: имя, режим, число правил, метки, переключатель «Используется»
Состояние: список из шаблона «Недоступные сайты» выбран для кадров
"""


def run(ui):
    ui.open_section("Main_RailRoutingTitle")
    name = ui.status_list_name()
    ui.scroll_into(lambda: ui.find_all(cls="TextBlock", label=name)[-1], 900)
    ui.save("routing-cards")
