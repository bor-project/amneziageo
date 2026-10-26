# -*- coding: utf-8 -*-
"""Подготовка стенда к языку кадров и разборка.

Стенд свой целиком (stand.sh): чистая библиотека агента, свои конфигурации STAND_CONFIGS из local/configs,
сеть до их сервера - kit/net.py, свой список из шаблона «Недоступные сайты» на языке кадров, светлая тема,
выбрана и подключена MAIN_CONFIG. После языка стенд гасится; библиотека стирается при следующем подъёме.
"""
import json
import os
import subprocess
import sys
import time

from ui import BASE, KIT, RUN, languages

STAND_CONFIGS = ("Frankfurt", "Helsinki")
MAIN_CONFIG = "Frankfurt"
CONFIGS = os.path.join(BASE, "local", "configs")


def net(action):
    """Сеть стенда до сервера конфигураций (kit/net.py), если её таблица заведена."""
    if os.path.exists(os.path.join(BASE, "local", "relay.json")):
        subprocess.run([sys.executable, os.path.join(KIT, "net.py"), action], check=action == "up")


def look(ui):
    ui.open_section("Main_RailGeneralTitle")
    combos = ui.find_all(cls="AdaptiveComboBox")
    return combos[0], combos[1]


def set_look(ui):
    theme, _ = look(ui)
    ui.pick(theme, ui.T_all("Theme_Light"))
    _, language = look(ui)
    ui.pick(language, [languages()[ui.lang]["native"]], wait=3)
    theme, language = look(ui)
    print("look: %s, %s" % (theme.shown, language.shown))


def lists(ui):
    return {str(item["id"]): item["name"] for item in ui.status().get("routingLists", [])}


def create_list(ui):
    """Создаёт список из шаблона «Недоступные сайты» так, как это делает пользователь, и возвращает его номер."""
    before = lists(ui)
    ui.open_section("Main_RailRoutingTitle")
    ui.click(key="Main_AddButton", wait=2)
    ui.pick(ui.after(ui.find(cls="TextBlock", key="Main_TemplateTitle"), cls="AdaptiveComboBox"), ui.T_all("Preset_ClosedName"))
    template = [node for node in ui.find_all(cls="TextBlock", key="Preset_ClosedName") if node.up("AdaptiveComboBox")]
    if not template:
        raise RuntimeError("template is not chosen")
    ui.click(key="Main_AddButton", wait=3)
    ui.click(key="Main_SaveButton", cls="AccessText", wait=3)
    added = [key for key in lists(ui) if key not in before]
    if len(added) != 1:
        raise RuntimeError("list was not created: %s" % added)
    return added[0]


def import_configs(ui):
    for name in STAND_CONFIGS:
        path = os.path.join(CONFIGS, name + ".conf")
        if not os.path.exists(path):
            raise SystemExit("no stand config %s (README, «Стенд»)" % path)
        print(ui.cli("config", "import", name, "--file", path).strip())


def connect(ui, name=MAIN_CONFIG, timeout=40):
    ui.cli("up", name)
    deadline = time.time() + timeout
    while not ui.connected() and time.time() < deadline:
        time.sleep(2)
    if not ui.connected():
        raise RuntimeError("%s did not connect" % name)


def sources(ui):
    return json.loads(ui.cli("--json", "source", "list"))


def geo(ui, timeout=1800):
    """Базы geo через туннель на свежем стенде, до первого списка: со списком DNS агента для имён вне списка идёт
    к системному резолверу напрямую, а напрямую у стенда сети нет. Ждёт, пока у всех источников будет дата."""
    print(ui.cli("geo", "update").strip())
    time.sleep(5)
    deadline = time.time() + timeout
    while time.time() < deadline:
        items = sources(ui)
        if not any(item["updating"] for item in items) and all(item["updated"] for item in items):
            break
        time.sleep(5)
    for item in sources(ui):
        print("geo %s: %s %s categories%s" % (item["name"], item["updated"], item["categoryCount"],
                                               ", error " + item["error"] if item["error"] else ""))


def docs_list(ui):
    """Список с именем шаблона на языке кадров: есть - он, нет - новый из шаблона; списки на других языках удаляются."""
    name = ui.T("Preset_ClosedName")
    found = [list_id for list_id, list_name in lists(ui).items() if list_name == name]
    chosen = found[0] if found else create_list(ui)
    for list_id in lists(ui):
        if list_id != chosen:
            print(ui.cli("routing", "remove", list_id).strip())
    return chosen


def setup(ui, keep=False):
    """Стенд к языку кадров. keep: на библиотеке прошлого подъёма (конфигурации и базы уже есть), без загрузки баз."""
    ui.stand("stop")
    kept = keep and os.path.isdir(os.path.join(RUN, "data", "geo"))
    print(ui.stand("start", env={"KEEP": "1" if kept else "0"}).strip(), "on the kept library" if kept else "")
    net("up")
    if not kept:
        import_configs(ui)
        ui.cli("routing", "use", "none")
        connect(ui)
        geo(ui)
    ui.restart()
    set_look(ui)
    ui.docs_list = docs_list(ui)
    ui.template_list = ui.docs_list
    print(ui.cli("routing", "use", ui.docs_list).strip())
    for name in STAND_CONFIGS:
        ui.cli("config", "routing", name, "on")
    connect(ui)
    ui.home()


def resume(ui):
    """Стенд уже подготовлен к языку: список кадров - выбранный."""
    ui.docs_list = str(ui.status().get("selectedRoutingList"))
    ui.template_list = ui.docs_list


def restore(ui):
    ui.stop_load()
    print(ui.stand("stop").strip())
    net("down")


def clean(ui):
    """Гасит стенд и стирает его библиотеку с ключами конфигураций."""
    restore(ui)
    subprocess.run(["sudo", "-n", "rm", "-rf", os.path.join(RUN, "data")], check=False)
    print("library removed")


def show(ui):
    print(ui.stand("members").strip() or "stand is down")
    if ui.stand("members").strip():
        print(ui.cli("--lang", "en", "status").strip())
