"""Где: menu/routing
Что видно: открытый список «Всё, кроме выбранного»: метка режима у заголовка и пояснение «Через VPN идут все приложения»
Состояние: список «Всё, кроме выбранного» есть или заводится на время кадра; ничего не сохраняется
"""


def run(ui):
    list_id, own = ui.full_list()
    try:
        ui.open_docs_editor(list_id)
        ui.find(cls="TextBlock", key="Main_AllAppsTitle", timeout=20)
        ui.save("routing-warning")
    finally:
        ui.drop_full_list(list_id, own)
