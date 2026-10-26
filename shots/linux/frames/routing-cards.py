"""Где: menu/routing
Что видно: карточки списков: имя, режим, число правил по ролям, метка «Все приложения через VPN», переключатель «Используется»
Состояние: список из шаблона «Недоступные сайты» выбран для кадров; список «Всё, кроме выбранного» есть или заводится на время кадра
"""


def run(ui):
    list_id, own = ui.full_list()
    try:
        ui.open_section("Main_RailRoutingTitle")
        name = ui.status_list_name()
        ui.find(cls="TextBlock", label=name)
        ui.save("routing-cards")
    finally:
        ui.drop_full_list(list_id, own)
