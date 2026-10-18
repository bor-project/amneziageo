"""Где: menu/routing
Что видно: «Дополнительно» у списка: весь UDP через туннель, время жизни маршрута, экспорт списка
Состояние: список из шаблона; ничего не меняется
"""


def run(ui):
    ui.open_docs_editor()
    title = ui.scroll_into(lambda: ui.find(cls="TextBlock", key="Main_AdvancedTitle", timeout=1), 300)
    ui.tap(title, wait=2)
    ui.scroll_into(lambda: ui.find(cls="TextBlock", key="Main_AdvancedTitle", timeout=1), 80)
    ui.save("routing-advanced")
