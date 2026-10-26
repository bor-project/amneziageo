"""Где: menu/routing
Что видно: «Дополнительно» у списка: весь UDP через туннель, время жизни маршрута, порог таблицы маршрутов, экспорт списка
Состояние: список из шаблона; ничего не меняется
"""


def run(ui):
    ui.open_docs_editor()
    ui.scroll_into(lambda: ui.find(cls="TextBlock", key="Main_AdvancedTitle", timeout=1), 1500)
    ui.click(key="Main_AdvancedTitle", cls="TextBlock", wait=2.5)
    ui.save("routing-advanced")
