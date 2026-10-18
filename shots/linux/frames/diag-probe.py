"""Где: menu/diagnostics
Что видно: проба www.youtube.com через туннель: отклик, приём и отдача, все проверки в норме
Состояние: путь «Туннель»: напрямую у стенда сети нет; журнал пробы очищен; проба повторяется, пока последняя
запись не «ok» во всех проверках; отдача меряется против адреса сервера в туннеле (10.0.0.1), он не размывается
"""
import re
import time

LOGS = r"^(ageo|resolver|routes|active|probe|config)$"
PATHS = ("Probe_PathAuto", "Probe_PathTunnel", "Probe_PathBypass")
STAMP = re.compile(r"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+ probe ")
LEG = re.compile(r"^\s+(reach|receive|send)\s+(\w+)\s", re.M)


def run(ui):
    ui.open_section("Main_RailLogsTitle")
    ui.pick(ui.find(cls="TextBlock", match=LOGS).up("AdaptiveComboBox"), ["probe"], wait=3)
    labels = [label for key in PATHS for label in ui.T_all(key)]
    path = next(node for node in ui.find_all(cls="AdaptiveComboBox") if node.shown in labels)
    ui.pick(path, ui.T_all("Probe_PathTunnel"))
    ui.set_field("Main_LogSearchWatermark", "youtube", wait=1)
    ui.set_field("Probe_TargetWatermark", "www.youtube.com", wait=1)
    ui.tap(ui.find(cls="Button", key="Main_ClearLogButton"), wait=2)
    for attempt in range(6):
        before = newest(ui)
        ui.click(key="Probe_RunButton", wait=1)
        deadline = time.time() + 60
        while newest(ui) == before and time.time() < deadline:
            time.sleep(4)
        legs = LEG.findall(newest(ui) or "")
        print("probe %d: %s" % (attempt + 1, " ".join("%s %s" % leg for leg in legs)))
        if len(legs) == 3 and all(state == "ok" for _, state in legs):
            break
        time.sleep(15)
    ui.save("diag-probe", keep=r"against 10\.0\.0\.1\b")


def newest(ui):
    """Первая запись журнала пробы: от первой строки с временем до следующей."""
    for node in ui.nodes():
        if node.cls == "SelectableTextBlock" and STAMP.match(node.text):
            entries = [line for line in node.text.split("\n")]
            starts = [i for i, line in enumerate(entries) if STAMP.match(line)]
            end = starts[1] if len(starts) > 1 else len(entries)
            return "\n".join(entries[starts[0]:end])
    return None
