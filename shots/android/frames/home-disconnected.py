"""Где: menu/home
Что видно: главный экран, туннель отключён, выбраны сервер и список маршрутизации
Состояние: туннель опускается на время кадра и поднимается обратно
"""
import time

import stand


def run(ui):
    ui.cli("down")
    time.sleep(4)
    ui.home()
    ui.save("home-disconnected")
    stand.connect(ui)
