"""Где: menu/index
Что видно: список разделов настроек на телефоне
Состояние: любое
"""


def run(ui):
    ui.open_settings()
    ui.save("menu")
