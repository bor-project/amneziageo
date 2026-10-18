"""Где: menu/diagnostics
Что видно: активные подключения с поиском youtube: адрес, домен, путь и причина
Состояние: через туннель идёт трафик youtube; таблица берёт снимок при выборе, поэтому выбирается заново до строк youtube
"""
import time

LOGS = r"^(ageo|resolver|routes|active|probe|config)$"


def run(ui):
    ui.start_load(120)
    time.sleep(10)
    ui.open_section("Main_RailLogsTitle")
    for attempt in range(6):
        ui.pick(ui.find(cls="TextBlock", match=LOGS).up("AdaptiveComboBox"), ["routes"], wait=1)
        ui.pick(ui.find(cls="TextBlock", match=LOGS).up("AdaptiveComboBox"), ["active"], wait=4)
        if attempt == 0:
            ui.set_field("Main_LogSearchWatermark", "youtube")
        if ui.has(match=r"youtube|ytimg|googlevideo"):
            break
    print("tries: %d" % (attempt + 1))
    ui.save("diag-active")
    ui.stop_load()
