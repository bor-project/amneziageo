# -*- coding: utf-8 -*-
"""Подготовка телефона к языку кадров и возврат.

На время языка: светлая тема, язык окна, свой список из шаблона, выбрана и подключена своя конфигурация.
После: всё, что было у владельца (тема, язык, выбор конфигурации и списка, туннель), свой список удалён.
Свои конфигурации (STAND_CONFIGS) живут до `shoot.py clean`.
"""
import json
import os
import re
import time

from ui import BASE, languages

STATE = os.path.join(BASE, "local", "state.json")
THEMES = ("Theme_System", "Theme_Light", "Theme_Dark")
STAND_CONFIGS = ("Frankfurt", "Helsinki")
MAIN_CONFIG = "Frankfurt"


def key_of(ui, label, keys):
    for key in keys:
        if label in ui.T_all(key):
            return key
    return None


def token(language):
    for code, info in languages().items():
        if info["native"] == language:
            return code
    return None


def look(ui):
    ui.open_section("Main_RailGeneralTitle")
    combos = ui.find_all(cls="AdaptiveComboBox")
    return combos[0], combos[1]


def read(ui):
    theme, language = look(ui)
    ui.window_lang = token(language.label)
    status = ui.status()
    return {
        "theme": key_of(ui, theme.label, THEMES),
        "language": key_of(ui, language.label, ["Lang_System"]) or language.label,
        "selected": status.get("selectedTarget"),
        "list": status.get("selectedRoutingList"),
        "connected": ui.connected(),
        "own_lists": [],
    }


def save(saved):
    with open(STATE, "w", encoding="utf-8") as target:
        json.dump(saved, target, ensure_ascii=False, indent=2)


def set_look(ui, theme_key, language):
    theme, _ = look(ui)
    ui.pick(theme, ui.T_all(theme_key))
    _, combo = look(ui)
    labels = ui.T_all(language) if language == "Lang_System" else [language]
    ui.pick(combo, labels, wait=3)
    ui.window_lang = token(language)
    theme, combo = look(ui)
    print("look: %s, %s" % (theme.label, combo.label))


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
    ui.hide_keyboard()
    ui.click(key="Main_SaveButton", cls="Button", wait=3)
    added = [key for key in lists(ui) if key not in before]
    if len(added) != 1:
        raise RuntimeError("list was not created: %s" % added)
    ui.home()
    return added[0]


def docs_lists(ui, saved):
    """Свой список из текущего шаблона (потом удаляется) и список для кадров: тот, что носит имя шаблона на языке кадров.

    Список владельца с именем шаблона только просматривается; кадрам, которым нужен текущий шаблон, - свой.
    """
    if not saved["own_lists"]:
        saved["own_lists"].append(create_list(ui))
        save(saved)
    template = saved["own_lists"][-1]
    name = ui.T("Preset_ClosedName")
    named = [list_id for list_id, list_name in lists(ui).items() if list_name == name]
    return (named[0] if named else template), template


def mask_owner_lists(ui, saved):
    """Список для кадров свой - значит, имени шаблона на этом языке у владельца нет: имена его списков размываются."""
    if ui.docs_list in saved["own_lists"]:
        ui.masks += ["^%s$" % re.escape(name) for list_id, name in lists(ui).items() if list_id not in saved["own_lists"]]


def resume(ui):
    """Телефон уже подготовлен к языку: списки и маски по записанному состоянию."""
    with open(STATE, encoding="utf-8") as source:
        saved = json.load(source)
    ui.template_list = saved["own_lists"][-1] if saved["own_lists"] else None
    ui.docs_list = str(ui.status().get("selectedRoutingList"))
    mask_owner_lists(ui, saved)


def setup(ui):
    _, language = look(ui)
    ui.window_lang = token(language.label)
    if not os.path.exists(STATE):
        saved = read(ui)
        save(saved)
        print("stand before: %s" % saved)
    with open(STATE, encoding="utf-8") as source:
        saved = json.load(source)
    names = [config["name"] for config in ui.status().get("configs", [])]
    missing = [name for name in STAND_CONFIGS if name not in names]
    if missing:
        raise SystemExit("no stand configs on the phone: %s (README, «Стенд»)" % ", ".join(missing))
    set_look(ui, "Theme_Light", languages()[ui.lang]["native"])
    ui.docs_list, ui.template_list = docs_lists(ui, saved)
    mask_owner_lists(ui, saved)
    print(ui.cli("routing", "use", ui.docs_list).strip())
    for name in STAND_CONFIGS:
        ui.cli("config", "routing", name, "on")
    connect(ui)
    ui.home()


def connect(ui, name=MAIN_CONFIG, timeout=40):
    ui.cli("up", name)
    deadline = time.time() + timeout
    while not ui.connected() and time.time() < deadline:
        time.sleep(2)
    if not ui.connected():
        raise RuntimeError("%s did not connect" % name)


def restore(ui):
    if not os.path.exists(STATE):
        print("nothing to restore")
        return
    with open(STATE, encoding="utf-8") as source:
        saved = json.load(source)
    set_look(ui, saved["theme"], saved["language"])
    if not saved["connected"]:
        ui.cli("down")
        time.sleep(3)
    if saved["selected"]:
        ui.cli("select", saved["selected"])
    ui.cli("routing", "use", str(saved["list"]) if saved["list"] else "none")
    for list_id in saved["own_lists"]:
        print(ui.cli("routing", "remove", list_id).strip())
    status = ui.status()
    now = {"selected": status.get("selectedTarget"), "list": status.get("selectedRoutingList"), "connected": ui.connected()}
    for name, value in now.items():
        if saved.get(name) != value:
            print("CHANGED %s: was %s, now %s" % (name, saved.get(name), value))
    ui.home()
    os.remove(STATE)
    print("stand restored: %s" % now)


def clean(ui):
    """Удаляет свои конфигурации стенда."""
    for name in STAND_CONFIGS:
        print(ui.cli("config", "remove", name).strip())


def show(ui):
    print(read(ui))
    ui.home()
