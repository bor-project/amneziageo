"""Где: menu/general
Что видно: экспорт: отмечены своя конфигурация и список маршрутизации из шаблона
Состояние: «Импорт / экспорт» в «Общих», «Экспорт…»; конфигурации владельца размыты; файл не сохраняется
"""
import stand


def run(ui):
    ui.open_section("Main_RailGeneralTitle")
    ui.scroll_into(lambda: ui.find(cls="TextBlock", key="Main_ImportExportSection", timeout=1), 900)
    ui.click(key="Main_ExportButton", wait=3)
    ui.scroll(-20000)
    ui.tap(ui.scroll_into(lambda: ui.find(cls="TextBlock", label=stand.MAIN_CONFIG, timeout=1), 1100), wait=1)
    name = ui.status_list_name()
    ui.scroll_into(lambda: ui.find_all(cls="TextBlock", label=name)[-1], 1700)
    ui.tap(ui.find_all(cls="TextBlock", label=name)[-1], wait=1)
    ui.scroll_into(lambda: ui.find(cls="TextBlock", label=stand.MAIN_CONFIG, timeout=1), 1100)
    ui.save("general-export")
    ui.close_page("BundleExport_WindowTitle")
