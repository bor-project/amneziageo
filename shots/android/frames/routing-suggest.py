"""Где: menu/routing
Что видно: подсказки при вводе netflix в «Что добавить»: точный домен, регион geoip, категория geosite
Состояние: первая карточка - список из шаблона; ввод стирается, список не сохраняется
"""


def run(ui):
    ui.open_docs_editor()
    field = ui.find(cls="TextBox", key="Main_AddEntryWatermark")
    ui.enter(field, "netflix")
    field = ui.scroll_into(lambda: ui.find(cls="TextBox", label="netflix"), 1450)
    ui.save("routing-suggest")
    ui.tap((field.box[2] - 80, (field.box[1] + field.box[3]) // 2))
