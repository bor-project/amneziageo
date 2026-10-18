"""Где: menu/routing
Что видно: подсказки при вводе netflix в «Что добавить»: точный домен, регион geoip, категория geosite
Состояние: список из шаблона; ввод не добавляется, список не сохраняется
"""


def run(ui):
    ui.open_docs_editor()
    ui.enter(ui.field("Main_AddEntryWatermark"), "netflix", wait=2)
    ui.save("routing-suggest")
    ui.key("Escape")
