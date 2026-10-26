# -*- coding: utf-8 -*-
"""Общие функции кадров Android: adb, дерево окна, нажатия, строки клиента, маски, сохранение."""
import json
import os
import re
import subprocess
import time
import xml.etree.ElementTree as ET

from PIL import Image, ImageFilter

KIT = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.dirname(KIT)
ADB = os.path.join(os.environ["LOCALAPPDATA"], "Android", "Sdk", "platform-tools", "adb.exe")
PACKAGE = "org.amneziageo.android"
CLI_ACTION = "org.amneziageo.android.CLI"
CLI_RECEIVER = "org.amneziageo.android/.CliReceiver"
LOC = "amneziageo-lib/AmneziaGeo.Localization"
SCALE = 2
WHEEL = 250
BOUNDS = re.compile(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]")


def read_json(path):
    with open(path, encoding="utf-8") as source:
        return json.load(source)


def languages():
    return read_json(os.path.join(BASE, "languages.json"))


class Node:
    """Элемент окна из дерева доступности."""

    def __init__(self, element, parent):
        self.cls = element.get("class", "")
        self.text = element.get("text", "")
        self.desc = element.get("content-desc", "")
        self.checked = element.get("checked") == "true"
        self.box = tuple(int(v) for v in BOUNDS.match(element.get("bounds", "[0,0][0,0]")).groups())
        self.parent = parent
        self.children = [Node(child, self) for child in element.findall("node")]

    @property
    def label(self):
        if self.text.startswith("Avalonia.") or " { " in self.text:
            return self.desc
        return self.text or self.desc

    @property
    def center(self):
        return (self.box[0] + self.box[2]) // 2, (self.box[1] + self.box[3]) // 2

    @property
    def visible(self):
        return self.box[2] > self.box[0] and self.box[3] > self.box[1]

    def walk(self):
        yield self
        for child in self.children:
            yield from child.walk()

    def up(self, cls):
        node = self.parent
        while node is not None and node.cls != cls:
            node = node.parent
        return node

    def __repr__(self):
        return "%s %r %s" % (self.cls, self.label, self.box)


class Ui:
    """Телефон стенда и язык кадров."""

    def __init__(self, lang):
        stand = read_json(os.path.join(BASE, "local", "stand.json"))
        self.client = stand["client"]
        self.serial = self._resolve(stand["serial"])
        self.lang = lang
        self.strings = {}
        self.masks = self._masks()
        self.root = None
        self.window_lang = None
        self.docs_list = None
        self.template_list = None

    # adb

    def _resolve(self, serial):
        listing = subprocess.run([ADB, "devices"], capture_output=True, text=True).stdout
        for line in listing.splitlines()[1:]:
            name = line.split("\t")[0]
            if serial in name and line.endswith("device"):
                return name
        raise SystemExit("device %s is not connected: adb mdns services, adb connect" % serial)

    def adb(self, *args, binary=False):
        done = subprocess.run([ADB, "-s", self.serial, *args], capture_output=True)
        return done.stdout if binary else done.stdout.decode("utf-8", "replace")

    def sh(self, command):
        return self.adb("shell", command)

    def cli(self, *args):
        """Команда консоли приложения; в процессе окна она ставит язык строк, поэтому идёт с языком окна."""
        if self.window_lang:
            args = ("--lang", self.window_lang) + args
        vector = ",".join(arg.replace(",", "\\,") for arg in args)
        reply = self.sh("am broadcast -a %s -n %s --esa args '%s'" % (CLI_ACTION, CLI_RECEIVER, vector.replace("'", "'\\''")))
        match = re.search(r'data="(.*)"\s*$', reply, re.S)
        return match.group(1) if match else reply

    def status(self):
        return json.loads(self.cli("--json", "status"))

    def connected(self):
        return re.search(r"^state\s+connected\s*$", self.cli("status"), re.M) is not None

    # строки клиента

    def T(self, key, lang=None):
        lang = lang or self.lang
        if lang not in self.strings:
            table = {}
            for name in ("Strings.resx", "Strings.%s.resx" % lang):
                text = subprocess.run(["git", "-C", self.client, "show", "HEAD:%s/%s" % (LOC, name)], capture_output=True).stdout
                if not text:
                    continue
                for data in ET.fromstring(text).findall("data"):
                    value = data.find("value")
                    table[data.get("name")] = value.text if value is not None and value.text else ""
            self.strings[lang] = table
        if key not in self.strings[lang]:
            raise KeyError("no string: %s" % key)
        return self.strings[lang][key]

    def T_all(self, key):
        return list(dict.fromkeys(self.T(key, lang) for lang in languages()))

    # дерево окна

    def dump(self):
        xml = self.sh("uiautomator dump /data/local/tmp/agshot.xml >/dev/null 2>&1; cat /data/local/tmp/agshot.xml; rm -f /data/local/tmp/agshot.xml")
        start = xml.find("<?xml")
        tree = ET.fromstring(xml[start:].encode("utf-8"))
        self.root = Node(tree.find("node"), None)
        return self.root

    def nodes(self, fresh=True):
        if fresh or self.root is None:
            self.dump()
        return [node for node in self.root.walk() if node.visible]

    def find_all(self, label=None, key=None, cls=None, fresh=True, match=None):
        labels = None
        if key is not None:
            labels = set(self.T_all(key))
        elif label is not None:
            labels = {label} if isinstance(label, str) else set(label)
        found = []
        for node in self.nodes(fresh):
            if cls is not None and node.cls != cls:
                continue
            if labels is not None and node.label not in labels:
                continue
            if match is not None and not re.search(match, node.label):
                continue
            found.append(node)
        return found

    def find(self, label=None, key=None, cls=None, timeout=8, match=None, index=0):
        deadline = time.time() + timeout
        while True:
            found = self.find_all(label, key, cls, match=match)
            if len(found) > index:
                return found[index]
            if time.time() > deadline:
                raise LookupError("not on screen: %s" % (key or label or match or cls))
            time.sleep(0.5)

    def after(self, anchor, label=None, key=None, cls=None):
        """Первый элемент после anchor в порядке дерева: кнопка внутри той же карточки."""
        nodes = self.nodes(fresh=False)
        start = next(i for i, node in enumerate(nodes) if node is anchor)
        labels = set(self.T_all(key)) if key is not None else ({label} if label is not None else None)
        for node in nodes[start + 1:]:
            if (cls is None or node.cls == cls) and (labels is None or node.label in labels):
                return node
        raise LookupError("nothing after %r" % anchor)

    def field(self, key, timeout=8):
        """Поле ввода по ключу его подсказки: подсказка остаётся в подписи и у заполненного поля."""
        labels = set(self.T_all(key))
        deadline = time.time() + timeout
        while True:
            found = [node for node in self.find_all(cls="TextBox") if node.desc in labels or node.text in labels]
            if found:
                return found[0]
            if time.time() > deadline:
                raise LookupError("no field: %s" % key)
            time.sleep(0.5)

    def set_field(self, key, text, wait=3.0):
        """Ставит в поле текст: стирает прежний и вводит новый."""
        node = self.field(key)
        if node.text and node.text not in self.T_all(key):
            if node.text == text:
                return node
            self.tap((node.box[2] - 80, (node.box[1] + node.box[3]) // 2))
            self.hide_keyboard()
            node = self.field(key)
        self.enter(node, text, wait)
        return self.field(key)

    def has(self, label=None, key=None, cls=None, match=None):
        return bool(self.find_all(label, key, cls, match=match))

    # нажатия

    def tap(self, target, wait=1.5):
        x, y = target.center if isinstance(target, Node) else target
        self.sh("input touchscreen swipe %d %d %d %d 150" % (x, y, x, y))
        time.sleep(wait)

    def click(self, label=None, key=None, cls=None, wait=1.5, timeout=8, index=0):
        node = self.find(label, key, cls, timeout=timeout, index=index)
        self.tap(node, wait)
        return node

    def swipe(self, x1, y1, x2, y2, ms=400, wait=1.0):
        self.sh("input touchscreen swipe %d %d %d %d %d" % (x1, y1, x2, y2, ms))
        time.sleep(wait)

    def scroll(self, pixels, wait=1.2):
        """Листает содержимое вверх на pixels (вниз при отрицательном) колесом мыши: шаг колеса - 250 точек."""
        self.sh("input mouse scroll 720 1600 --axis VSCROLL,%.2f" % (-pixels / WHEEL))
        time.sleep(wait)

    def scroll_to(self, label=None, key=None, cls=None, tries=8, bottom=2700):
        for _ in range(tries):
            found = [node for node in self.find_all(label, key, cls) if node.box[3] < bottom]
            if found:
                return found[0]
            self.scroll(900)
        raise LookupError("not found by scrolling: %s" % (key or label))

    def scroll_into(self, find, top, slack=150, tries=5):
        """Листает, пока верх элемента, который возвращает find(), не встанет около top."""
        steps = [1200] * 8 + [-1200] * 16
        for step in steps:
            try:
                node = find()
                break
            except LookupError:
                self.scroll(step)
        else:
            node = find()
        delta = 0
        for _ in range(tries):
            delta = node.box[1] - top
            if abs(delta) <= slack:
                break
            self.scroll(delta)
            try:
                node = find()
            except LookupError:
                self.scroll(-delta)
                node = find()
        return node

    def type_text(self, text, wait=1.0):
        self.sh("input text '%s'" % text.replace(" ", "%s").replace("'", ""))
        time.sleep(wait)

    def keyboard_shown(self):
        return "mInputShown=true" in self.sh("dumpsys input_method | grep mInputShown")

    def enter(self, field, text, wait=3.0):
        """Вводит текст в поле и прячет клавиатуру."""
        self.tap(field, wait=1.0)
        deadline = time.time() + 6
        while not self.keyboard_shown() and time.time() < deadline:
            time.sleep(0.5)
        self.type_text(text, wait=wait)
        self.hide_keyboard()

    def hide_keyboard(self):
        """Прячет клавиатуру клавишей Esc: «Назад» в момент ввода закрывает и раздел."""
        if self.keyboard_shown():
            self.sh("input keyevent 111")
            time.sleep(1.5)

    def back(self, wait=1.2):
        self.sh("input keyevent 4")
        time.sleep(wait)

    def start_load(self, seconds=30):
        """Гоняет запросы к youtube через туннель, чтобы у подключения были скорость, журнал и активные соединения."""
        urls = " ".join(("https://www.youtube.com/", "https://m.youtube.com/", "https://i.ytimg.com/generate_204"))
        loop = "end=$(( $(date +%%s) + %d )); while [ $(date +%%s) -lt $end ]; do for u in %s; do curl -s -o /dev/null -m 10 $u; done; done" % (seconds, urls)
        self.load = subprocess.Popen([ADB, "-s", self.serial, "shell", loop], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def stop_load(self):
        if getattr(self, "load", None) is not None:
            self.load.kill()
            self.load = None

    # приложение

    def start(self):
        self.sh("am start -n %s/crc64d1d6e1e305fa4de9.MainActivity >/dev/null" % PACKAGE)
        time.sleep(2)

    def home(self, tries=8):
        """Возвращает окно на главный экран."""
        self.start()
        for _ in range(tries):
            if self.is_home():
                return
            self.back()
        if not self.is_home():
            raise RuntimeError("main screen did not come back")

    def open_settings(self):
        self.home()
        self.click(key="Main_SettingsTooltip", wait=2)

    SUBPAGES = ("BundleExport_WindowTitle", "BundleImport_WindowTitle")

    def open_section(self, key):
        """Открывает раздел настроек по ключу его пункта в меню; вложенную страницу, которую раздел помнит, закрывает."""
        self.open_settings()
        self.click(key=key, wait=2)
        for title in self.SUBPAGES:
            if self.has(cls="TextBlock", key=title) and not self.has(cls="TextBlock", key=key):
                self.close_page(title)

    def open_editor(self, index=0):
        """Открывает редактор списка маршрутизации шестерёнкой его карточки."""
        self.open_section("Main_RailRoutingTitle")
        self.click(key="Main_CardSettingsLink", cls="Button", wait=2.5, index=index)

    def open_docs_editor(self, list_id=None):
        """Открывает редактор списка шестерёнкой его карточки: по умолчанию выбранного для кадров (stand.docs_list)."""
        self.open_section("Main_RailRoutingTitle")
        name = self.status_list_name(list_id)
        card = self.scroll_into(lambda: self.find_all(cls="TextBlock", label=name)[-1] if self.find_all(cls="TextBlock", label=name)
                                else self.find(cls="TextBlock", label=name, timeout=1), 900)
        self.tap(self.after(card, key="Main_CardSettingsLink", cls="Button"), wait=2.5)

    REGIONS = {"ru": "ru", "en": "us"}

    def full_list(self):
        """Список «Всё, кроме выбранного» с именем шаблона на языке кадров: уже заведённый или свой на время кадра;
        номер и признак своего."""
        name = self.T("Preset_AllButLocalName")
        held = [str(item["id"]) for item in self.status().get("routingLists", [])
                if item.get("useGlobalProxy") and item["name"] == name]
        if held:
            return held[0], False
        self.cli("routing", "create", name, "direct|geoip:%s" % self.REGIONS.get(self.lang, self.lang))
        self.cli("routing", "configure", name, "--global-proxy", "on")
        time.sleep(2)
        made = [str(item["id"]) for item in self.status().get("routingLists", [])
                if item.get("useGlobalProxy") and item["name"] == name]
        if not made:
            raise RuntimeError("the full list was not created")
        return made[0], True

    def drop_full_list(self, list_id, own):
        """Удаляет свой список «Всё, кроме выбранного»; чужой остаётся."""
        if own:
            self.cli("routing", "remove", list_id)

    def status_list_name(self, list_id=None):
        status = self.status()
        wanted = str(list_id or self.docs_list or status.get("selectedRoutingList"))
        for item in status.get("routingLists", []):
            if str(item["id"]) == wanted:
                return item["name"]
        raise LookupError("no routing list %s" % wanted)

    def close_page(self, title_key):
        """Закрывает вложенную страницу раздела стрелкой слева от её заголовка: раздел помнит открытую страницу."""
        title = self.find(cls="TextBlock", key=title_key)
        arrows = [node for node in self.find_all(cls="Button", fresh=False)
                  if node.box[2] <= title.box[0] and node.box[1] <= title.center[1] <= node.box[3]]
        self.tap(arrows[-1], wait=2)

    def pick(self, combo, labels, wait=1.5):
        """Выбирает пункт выпадающего списка по одной из подписей."""
        labels = [labels] if isinstance(labels, str) else list(labels)
        node = self.find(label=combo) if isinstance(combo, str) else combo
        if node.label in labels:
            return node.label
        self.tap(node)
        items = [item for item in self.find_all(label=labels, cls="TextBlock") if item.up("AdaptiveComboBox") is None]
        if not items:
            self.back()
            raise LookupError("no item: %s" % " | ".join(labels))
        self.tap(items[-1], wait)
        return items[-1].label

    def is_home(self):
        nodes = self.nodes()
        return not any(node.cls == "ConnectPill" for node in nodes) and any(node.cls == "MainView" for node in nodes)

    # кадр

    def content_box(self):
        for node in self.root.walk():
            if node.cls == "LayoutTransformControl":
                return node.box
        return self.root.box

    def mask_boxes(self, extra=(), keep=None):
        """Границы элементов, чей текст подходит под маски; элементы под выдвижной панелью не трогает."""
        nodes = [node for node in self.root.walk() if node.visible]
        content = self.content_box()
        sheets = [(i, node) for i, node in enumerate(nodes)
                  if node.cls == "Border" and node.parent is not None and node.parent.box == content and node.box != content]
        boxes = []
        for index, node in enumerate(nodes):
            label = node.text or node.desc
            if not label or not any(re.search(pattern, label) for pattern in self.masks):
                continue
            if keep and re.search(keep, label):
                continue
            lines = label.split("\n")
            if len(lines) > 1:
                boxes.append((node, lines, keep))
                continue
            x, y = node.center
            covered = any(i > index and not self._inside(node, sheet)
                          and sheet.box[0] <= x <= sheet.box[2] and sheet.box[1] <= y <= sheet.box[3]
                          for i, sheet in sheets)
            visible = self._clip(node)
            if not covered and visible:
                boxes.append(visible)
        boxes.extend(extra)
        return boxes

    def _line_boxes(self, image, node, lines, keep):
        """Строки многострочного текста, подходящие под маски, по полосам текста на снимке; не сошлось - весь текст."""
        left, top, right, bottom = node.box
        region = image.crop(node.box).convert("L")
        width, height = region.size
        pixels = region.load()
        background = max(region.getcolors(width * height) or [(0, 255)])[1]
        ink = [any(abs(pixels[x, y] - background) > 60 for x in range(0, width, 2)) for y in range(height)]
        bands = []
        for y, inked in enumerate(ink):
            if inked and bands and y - bands[-1][1] <= 6:
                bands[-1][1] = y
            elif inked:
                bands.append([y, y])
        text = [line for line in lines if line.strip()]
        if len(bands) != len(text):
            print("line mask: %d bands for %d lines, whole text blurred" % (len(bands), len(text)))
            return [node.box]
        boxes = []
        for line, (first, last) in zip(text, bands):
            if any(re.search(pattern, line) for pattern in self.masks) and not (keep and re.search(keep, line)):
                boxes.append((left, top + first - 8, right, top + last + 8))
        return boxes

    @staticmethod
    def _clip(node):
        """Видимая часть элемента в его прокрутке: у ушедшего за край дерево даёт границы не там, где он нарисован."""
        left, top, right, bottom = node.box
        parent = node.parent
        while parent is not None:
            if parent.cls == "ScrollViewer":
                left, top = max(left, parent.box[0]), max(top, parent.box[1])
                right, bottom = min(right, parent.box[2]), min(bottom, parent.box[3])
            parent = parent.parent
        if right - left < 8 or bottom - top < 8:
            return None
        return left, top, right, bottom

    @staticmethod
    def _inside(node, ancestor):
        while node is not None:
            if node is ancestor:
                return True
            node = node.parent
        return False

    def _masks(self):
        path = os.path.join(BASE, "local", "mask.txt")
        if not os.path.exists(path):
            return []
        with open(path, encoding="utf-8") as source:
            return [line.strip() for line in source if line.strip() and not line.startswith("#")]

    def heads_up(self):
        """Всплывающее уведомление поверх окна: в кадр попадут чужие сообщения."""
        state = self.sh("dumpsys activity service com.android.systemui/.SystemUIService 2>/dev/null"
                        " | grep -E 'mHeadsUpVisible=|mHeadsUpPinnedMode=|hasPinnedHeadsUpRow='")
        return "=true" in state or not state.strip()

    def capture(self, timeout=90):
        """Снимок экрана без всплывающих уведомлений: ждёт, пока они уйдут, и переснимает, если всплыли во время снимка."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.heads_up():
                time.sleep(3)
                continue
            png = self.adb("exec-out", "screencap -p", binary=True)
            if not self.heads_up():
                return png
        raise RuntimeError("a notification stays over the window")

    def save(self, name, extra=(), wait=1.0, keep=None):
        time.sleep(wait)
        if self.heads_up():
            self.capture()
        self.dump()
        boxes = self.mask_boxes(extra, keep)
        png = self.capture()
        folder = os.path.join(BASE, "out", self.lang)
        os.makedirs(folder, exist_ok=True)
        raw = os.path.join(folder, name + ".raw.png")
        with open(raw, "wb") as target:
            target.write(png)
        image = Image.open(raw).convert("RGB")
        boxes = [box for item in boxes for box in (self._line_boxes(image, *item) if isinstance(item[0], Node) else [item])]
        for box in boxes:
            region = image.crop(box)
            region = region.filter(ImageFilter.GaussianBlur(28))
            image.paste(region, box[:2])
        left, top, right, bottom = self.content_box()
        image = image.crop((left, top, right, bottom))
        image = image.resize((image.width // SCALE, image.height // SCALE), Image.LANCZOS)
        path = os.path.join(folder, name + ".png")
        image.save(path, optimize=True)
        os.remove(raw)
        print("saved %s %dx%d masks %d" % (path, image.width, image.height, len(boxes)))
        return path


def tree(ui):
    root = ui.dump()

    def show(node, depth):
        if node.visible and (node.label or node.cls not in ("ContentPresenter", "Border", "Panel", "Grid", "StackPanel")):
            print("%s%s %r %s%s" % ("  " * depth, node.cls, node.label, node.box, " checked" if node.checked else ""))
        for child in node.children:
            show(child, depth + 1)

    show(root, 0)


def steps(ui, args):
    """Шаги для проб: k=<ключ> t=<подпись> back home scroll=<px> sleep=<с> tree shot=<имя> key=<текст>."""
    for arg in args:
        name, _, value = arg.partition("=")
        if name == "k":
            print("tap", ui.click(key=value))
        elif name == "t":
            print("tap", ui.click(label=value))
        elif name == "back":
            ui.back()
        elif name == "home":
            ui.home()
        elif name == "scroll":
            ui.scroll(int(value))
        elif name == "sleep":
            time.sleep(float(value))
        elif name == "tree":
            tree(ui)
        elif name == "shot":
            ui.save(value)
        elif name == "key":
            ui.T("Main_SaveButton")
            for key, text in ui.strings[ui.lang].items():
                if text and value.lower() in text.lower():
                    print("%s = %s" % (key, text[:100]))
        else:
            raise SystemExit("unknown step: %s" % arg)


if __name__ == "__main__":
    import sys

    steps(Ui(sys.argv[1]), sys.argv[2:] or ["tree"])
