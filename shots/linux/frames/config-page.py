"""Где: menu/configurations
Что видно: страница конфигурации: имя, текст (скрыт), транспорт: прокси, MTU, IPv6
Состояние: своя подключённая конфигурация; ничего не меняется
"""
import stand


def run(ui):
    ui.open_section("Main_RailConfigTitle")
    name = ui.find(cls="TextBlock", label=stand.MAIN_CONFIG)
    ui.tap(ui.after(name, key="Main_CardSettingsLink"), wait=2)
    ui.save("config-page")
