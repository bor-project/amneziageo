"""Где: android/recipes/remote-network
Что видно: подсказка cidr:10.8.0.0/24 при вводе диапазона 10.8.0.0/24 в редакторе списка
Состояние: диапазон набран, но не добавляется; «cidr:» не набирается: клавиатура исправляет его в «cider»; адрес из примера рецепта не размывается
"""


def run(ui):
    ui.open_docs_editor()
    ui.enter(ui.find(cls="TextBox", key="Main_AddEntryWatermark"), "10.8.0.0/24")
    field = ui.scroll_into(lambda: ui.find(cls="TextBox", label="10.8.0.0/24"), 1450)
    ui.save("routing-cidr", keep=r"10\.8\.0\.0/24")
    ui.tap((field.box[2] - 80, (field.box[1] + field.box[3]) // 2))
    ui.hide_keyboard()
