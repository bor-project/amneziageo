"""Где: menu/diagnostics
Что видно: проба www.youtube.com через туннель: отклик, приём и отдача, все проверки в норме
Состояние: проба запущена и дождалась ответа; проба с обрывом повторяется
"""
import time

LOGS = r"^(ageo|routes|active|probe|config)$"


def run(ui):
    ui.open_section("Main_RailLogsTitle")
    ui.pick(ui.find(cls="AdaptiveComboBox", match=LOGS), ["probe"], wait=3)
    ui.set_field("Main_LogSearchWatermarkShort", "youtube", wait=1)
    ui.set_field("Probe_TargetWatermark", "www.youtube.com", wait=1)
    bad = set(ui.T_all("Probe_StateBad"))
    for attempt in range(5):
        before = newest(ui)
        ui.click(key="Probe_RunButton", cls="Button", wait=1)
        deadline = time.time() + 60
        while newest(ui) == before and time.time() < deadline:
            time.sleep(5)
        time.sleep(5)
        verdicts = entry(ui)
        if verdicts and not bad & set(verdicts):
            break
        time.sleep(20)
    print("probe tries: %d" % (attempt + 1))
    ui.save("diag-probe")


def entry(ui):
    """Подписи первой записи журнала пробы: до времени второй записи."""
    labels = [node.label for node in ui.nodes() if node.cls == "TextBlock" and node.box[1] > 1900]
    times = [i for i, label in enumerate(labels) if label.count(":") == 2 and label.replace(":", "").replace(".", "").isdigit()]
    return labels[times[0]:times[1]] if len(times) > 1 else labels[times[0]:] if times else []


def newest(ui):
    labels = entry(ui)
    return labels[0] if labels else None
