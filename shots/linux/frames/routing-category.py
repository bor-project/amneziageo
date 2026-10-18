"""Где: menu/routing
Что видно: раскрытая запись geosite:youtube: число записей, диапазоны адресов и домены
Состояние: список из шаблона; адреса диапазонов из баз видны, ничего не сохраняется
"""


def run(ui):
    ui.open_docs_editor()
    entry = ui.scroll_into(lambda: ui.find(cls="TextBlock", label="geosite:youtube", timeout=1), 140)
    ui.tap(ui.after(entry, key="Main_RuleEntriesTooltip"), wait=2.5)
    ui.scroll_into(lambda: ui.find(cls="TextBlock", label="geosite:youtube", timeout=1), 110)
    ui.save("routing-category")
