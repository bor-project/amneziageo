"""Где: menu/general
Что видно: импорт: поведение при совпадении имён, поле для вставки, выбор файла
Состояние: «Импорт / экспорт» в «Общих», «Импорт…»; ничего не импортируется
"""


def run(ui):
    ui.open_section("Main_RailGeneralTitle")
    ui.scroll_into(lambda: ui.find(cls="TextBlock", key="Main_ImportExportSection", timeout=1), 900)
    ui.click(key="Main_ImportButton", wait=3)
    ui.scroll(-20000)
    ui.save("general-import")
    ui.close_page("BundleImport_WindowTitle")
