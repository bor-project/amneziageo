"""Где: menu/geo-sources
Что видно: источники geo: автообновление, добавление базы, список источников с датами
Состояние: базы скачаны при подготовке стенда; раздел открывается кнопкой «Источники geo» в редакторе списка
"""


def run(ui):
    ui.open_docs_editor()
    ui.click(key="Main_SourcesSection", wait=2)
    ui.save("geo-sources")
