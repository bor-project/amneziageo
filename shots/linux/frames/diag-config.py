"""Где: menu/diagnostics
Что видно: текущая конфигурация: состояние, маршрутизация и транспорт
Состояние: строки с ключами размыты
"""
LOGS = r"^(ageo|resolver|routes|active|probe|config)$"


def run(ui):
    ui.open_section("Main_RailLogsTitle")
    ui.pick(ui.find(cls="TextBlock", match=LOGS).up("AdaptiveComboBox"), ["config"], wait=4)
    ui.save("diag-config")
