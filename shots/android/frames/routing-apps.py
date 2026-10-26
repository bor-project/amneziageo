"""Где: menu/routing
Что видно: выбор приложений для правила, поиск youtube
Состояние: список из шаблона; окно закрывается «Отменой», «Что добавить» возвращается на «Адрес»
"""


def run(ui):
    ui.open_docs_editor()
    kind = ui.scroll_into(lambda: ui.find(cls="AdaptiveComboBox", key="Main_AddByAddressTab", timeout=1), 1200)
    ui.pick(kind, ui.T_all("Main_AddByAppTab"))
    ui.click(key="Main_AppsButton", cls="Button", wait=3)
    ui.enter(ui.find(cls="TextBox", key="AppPicker_Search"), "youtube")
    ui.save("routing-apps")
    ui.click(key="Main_CancelButton", cls="Button", wait=2)
    ui.pick(ui.find(cls="AdaptiveComboBox", key="Main_AddByAppTab"), ui.T_all("Main_AddByAddressTab"))
