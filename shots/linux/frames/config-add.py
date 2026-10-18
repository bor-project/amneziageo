"""Где: menu/configurations, linux/first-config
Что видно: меню «Добавить» в каталоге: загрузить файл, вставить, создать вручную
Состояние: любое; ничего не добавляется, меню закрывается Esc
"""


def run(ui):
    ui.open_section("Main_RailConfigTitle")
    ui.click(key="Main_AddButton", wait=1.5)
    ui.save("config-add")
    ui.key("Escape")
