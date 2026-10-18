"""Где: menu/general
Что видно: раздел «Общие»: тема, язык интерфейса, версия, обновление
Состояние: светлая тема и язык кадров выбраны при подготовке стенда
"""


def run(ui):
    ui.open_section("Main_RailGeneralTitle")
    ui.save("general")
