"""Где: menu/general
Что видно: раздел «Общие»: тема, язык интерфейса, предупреждения о маршрутизации, работа в фоне, версия
Состояние: любое
"""


def run(ui):
    ui.open_section("Main_RailGeneralTitle")
    ui.scroll(-4000)
    ui.save("general")
