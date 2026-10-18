"""Где: menu/diagnostics
Что видно: журнал маршрутов с поиском youtube: разрешённые адреса и решение по правилу
Состояние: «Записывать подключения в журнал» включено до нагрузки; через туннель идёт трафик youtube
"""
import time

LOGS = r"^(ageo|resolver|routes|active|probe|config)$"


def run(ui):
    ui.open_section("Main_RailLogsTitle")
    ui.pick(ui.find(cls="TextBlock", match=LOGS).up("AdaptiveComboBox"), ["routes"], wait=2)
    switch = ui.after(ui.find(cls="TextBlock", key="Main_RouteLogTitle"), cls="ToggleSwitch")
    if not switch.checked:
        ui.tap(switch, wait=1.5)
    ui.start_load(40)
    time.sleep(15)
    ui.set_field("Main_LogSearchWatermark", "youtube", wait=6)
    ui.save("diag-routes")
    ui.stop_load()
