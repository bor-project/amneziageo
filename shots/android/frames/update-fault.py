"""Где: menu/home, «Ошибка проверки обновлений»
Что видно: главный экран, знак у состояния и шторка с причиной и «Скрыть»
Состояние: как у update-source: проверка не удалась, сервер не отвечает; знак не скрывается
"""


def run(ui):
    ui.home()
    ui.tap(ui.find(cls="UpdateFaultHint", timeout=60), wait=2)
    ui.find(key="Main_UpdateFaultHide")
    ui.save("update-fault")
    ui.back()
