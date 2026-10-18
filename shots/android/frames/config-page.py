"""Где: menu/configurations
Что видно: страница конфигурации: «Используется», имя, текст (скрыт), транспорт: прокси, MTU, IPv6
Состояние: своя подключённая конфигурация; ничего не меняется
"""
import stand


def run(ui):
    ui.open_section("Main_RailConfigTitle")
    name = ui.scroll_into(lambda: ui.find(cls="TextBlock", label=stand.MAIN_CONFIG, timeout=1), 560)
    ui.tap(ui.after(name, key="Main_CardSettingsLink", cls="Button"), wait=2.5)
    ui.scroll(-3000)
    ui.save("config-page")
