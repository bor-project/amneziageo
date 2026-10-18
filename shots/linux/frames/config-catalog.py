"""Где: menu/configurations
Что видно: каталог конфигураций: карточки с адресом, откликом и метками, подключённая и соседняя
Состояние: на стенде только свои конфигурации; без нагрузки; потери в карточке считаются и по прошлым замерам,
поэтому кадр ждёт, пока у подключённой они не станут 0%
"""
import re
import time

import stand


def run(ui):
    ui.open_section("Main_RailConfigTitle")
    for attempt in range(15):
        value = loss(ui)
        if value == 0:
            break
        time.sleep(20)
    print("catalog loss %s%% after %d checks" % (value, attempt + 1))
    ui.save("config-catalog", wait=2)


def loss(ui):
    """Потери в карточке подключённой конфигурации: первый текст вида N% после её имени."""
    nodes = ui.nodes()
    start = next(i for i, node in enumerate(nodes) if node.cls == "TextBlock" and node.label == stand.MAIN_CONFIG)
    for node in nodes[start + 1:]:
        if node.cls == "TextBlock" and re.fullmatch(r"\d+%", node.label):
            return int(node.label[:-1])
    return None
