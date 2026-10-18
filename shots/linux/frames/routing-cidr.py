"""Где: linux/recipes/remote-network
Что видно: подсказка cidr:10.8.0.0/24 при вводе диапазона 10.8.0.0/24 в редакторе списка
Состояние: диапазон набран, но не добавляется; адрес из примера рецепта не размывается
"""


def run(ui):
    ui.open_docs_editor()
    ui.enter(ui.field("Main_AddEntryWatermark"), "10.8.0.0/24", wait=2)
    ui.save("routing-cidr", keep=r"10\.8\.0\.0/24")
    ui.key("Escape")
