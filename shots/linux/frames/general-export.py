"""Где: menu/general
Что видно: экспорт: отмечены своя конфигурация и список маршрутизации из шаблона
Состояние: «Импорт / экспорт» в «Общих», «Экспорт…»; файл не сохраняется
"""
import stand


def run(ui):
    ui.open_section("Main_RailGeneralTitle")
    ui.click(key="Main_ExportButton", wait=2.5)
    ui.tap(ui.find(cls="TextBlock", label=stand.MAIN_CONFIG), wait=1)
    ui.tap(ui.find_all(cls="TextBlock", label=ui.status_list_name())[-1], wait=1)
    ui.save("general-export")
    ui.close_page("BundleExport_WindowTitle")
