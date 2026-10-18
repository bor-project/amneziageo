"""Где: menu/diagnostics
Что видно: журнал маршрутов с поиском youtube: разрешённые адреса и решение по правилу
Состояние: через туннель идёт трафик youtube
"""
import time


def run(ui):
    ui.start_load(40)
    ui.open_section("Main_RailLogsTitle")
    ui.pick(ui.find(cls="AdaptiveComboBox", match=r"^(ageo|routes|active|probe|config)$"), ["routes"], wait=2)
    ui.set_field("Main_LogSearchWatermarkShort", "youtube", wait=6)
    ui.save("diag-routes")
    ui.stop_load()
