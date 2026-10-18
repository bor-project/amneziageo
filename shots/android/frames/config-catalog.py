"""Где: menu/configurations
Что видно: каталог конфигураций в конце: карточки с откликом и потерями, подключённая и соседняя
Состояние: свои конфигурации стоят последними; карточки и адреса владельца размыты
"""
import stand


def run(ui):
    ui.open_section("Main_RailConfigTitle")
    ui.scroll_into(lambda: ui.find(cls="TextBlock", label=stand.MAIN_CONFIG, timeout=1), 560)
    ui.save("config-catalog", wait=4)
