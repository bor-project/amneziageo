"""Где: menu/general
Что видно: импорт: поведение при совпадении имён, поле для вставки, выбор файла
Состояние: «Импорт / экспорт» в «Общих», «Импорт…»; ничего не импортируется
"""


def run(ui):
    ui.open_section("Main_RailGeneralTitle")
    ui.click(key="Main_ImportButton", wait=2.5)
    ui.save("general-import")
    ui.close_page("BundleImport_WindowTitle")
