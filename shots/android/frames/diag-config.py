"""Где: menu/diagnostics
Что видно: текущая конфигурация: состояние, маршрутизация и транспорт
Состояние: строки с ключами и адресами стенда размыты
"""


def run(ui):
    ui.open_section("Main_RailLogsTitle")
    ui.pick(ui.find(cls="AdaptiveComboBox", match=r"^(ageo|routes|active|probe|config)$"), ["config"], wait=4)
    ui.save("diag-config")
