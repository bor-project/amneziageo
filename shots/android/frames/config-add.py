"""Где: menu/configurations, android/first-config
Что видно: «Добавить конфигурацию»: загрузить файл, вставить, сканировать QR-код, создать вручную
Состояние: каталог в начале; карточка владельца под панелью размыта; ничего не добавляется
"""


def run(ui):
    ui.open_section("Main_RailConfigTitle")
    ui.scroll(-3000)
    ui.click(key="Main_AddButton", wait=2)
    ui.save("config-add")
    ui.back()
