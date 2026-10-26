# -*- coding: utf-8 -*-
"""Общие функции кадров Linux: стенд, дерево окна от помощника, нажатия, строки клиента, маски, сохранение."""
import io
import json
import os
import re
import subprocess
import time
import xml.etree.ElementTree as ET

from PIL import Image, ImageFilter

KIT = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.dirname(KIT)
STAND = os.path.join(KIT, "stand.sh")
RUN = "/var/tmp/agdocs/run"
HOOK = os.path.join(RUN, "hook")
LOC = "amneziageo-lib/AmneziaGeo.Localization"
CLIENT = os.environ.get("CLIENT", "/home/bor/dev/amneziageo")
TITLE = "AmneziaGeo"
SIZE = (984, 752)
AWAY = (1590, 990)


def read_json(path):
    with open(path, encoding="utf-8") as source:
        return json.load(source)


def languages():
    return read_json(os.path.join(BASE, "languages.json"))


class Node:
    """Элемент окна из дерева помощника."""

    def __init__(self, item, parent):
        self.id = item["i"]
        self.cls = item["t"]
        self.name = item.get("n", "")
        self.classes = item.get("c", "")
        self.text = item.get("text", "")
        self.hint = item.get("hint", "")
        self.tip = item.get("tip", "")
        self.checked = item.get("chk")
        self.selected = item.get("sel")
        self.off = item.get("off", False)
        self.box = tuple(int(v) for v in item["b"])
        self.seen = tuple(int(v) for v in item["v"])
        self.win = item["win"]
        self.offset = item.get("oy")
        self.extent = item.get("ey")
        self.viewport = item.get("vy")
        self.parent = parent
        self.children = []

    @property
    def label(self):
        return self.text or self.hint or self.tip

    @property
    def shown(self):
        """Подпись элемента или первая подпись внутри него: выбранный пункт выпадающего списка."""
        return self.label or next((node.label for node in self.walk() if node.cls == "TextBlock" and node.label), "")

    @property
    def center(self):
        left, top, right, bottom = self.seen if self.visible else self.box
        return (left + right) // 2, (top + bottom) // 2

    @property
    def visible(self):
        return self.seen[2] - self.seen[0] > 1 and self.seen[3] - self.seen[1] > 1

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
        return "%s %r %s" % (self.cls, self.label, self.seen)


class Ui:
    """Окно стенда и язык кадров."""

    def __init__(self, lang):
        self.lang = lang
        self.locale = languages()[lang]["locale"]
        self.strings = {}
        self.masks = self._masks()
        self.roots = []
        self.windows = []
        self.wids = {}
        self.docs_list = None
        self.template_list = None
        self.load = None

    # стенд

    def stand(self, *args, binary=False, env=None):
        done = subprocess.run(["bash", STAND, *args], capture_output=True, env=dict(os.environ, **(env or {})))
        return done.stdout if binary else done.stdout.decode("utf-8", "replace")

    def inside(self, *args, binary=False):
        return self.stand("in", *[str(arg) for arg in args], binary=binary)

    def cli(self, *args):
        return self.stand("cli", "--lang", self.lang, *args)

    def status(self):
        return json.loads(self.cli("--json", "status"))

    def connected(self):
        return re.search(r"^state\s+connected\s*$", self.cli("--lang", "en", "status"), re.M) is not None

    # строки клиента

    def T(self, key, lang=None):
        lang = lang or self.lang
        if lang not in self.strings:
            table = {}
            for name in ("Strings.resx", "Strings.%s.resx" % lang):
                text = subprocess.run(["git", "-C", CLIENT, "show", "HEAD:%s/%s" % (LOC, name)], capture_output=True).stdout
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

    # помощник в окне

    def ask(self, command, timeout=15):
        answer = os.path.join(HOOK, "res")
        if os.path.exists(answer):
            os.remove(answer)
        temp = os.path.join(HOOK, "req.tmp")
        with open(temp, "w", encoding="utf-8") as target:
            target.write(command)
        os.replace(temp, os.path.join(HOOK, "req"))
        deadline = time.time() + timeout
        while not os.path.exists(answer):
            if time.time() > deadline:
                raise TimeoutError("window helper is silent: %s" % command)
            time.sleep(0.05)
        reply = read_json(answer)
        os.remove(answer)
        if "error" in reply:
            raise RuntimeError("window helper: %s" % reply["error"][:400])
        return reply

    def dump(self):
        data = self.ask("tree")
        self.windows = data["windows"]
        nodes = {}
        self.roots = []
        for item in data["nodes"]:
            parent = nodes.get(item["p"])
            node = Node(item, parent)
            nodes[node.id] = node
            if parent is None:
                self.roots.append(node)
            else:
                parent.children.append(node)
        return self.roots

    def nodes(self, fresh=True):
        if fresh or not self.roots:
            self.dump()
        return [node for root in self.roots for node in root.walk() if node.visible]

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
            time.sleep(0.4)

    def after(self, anchor, label=None, key=None, cls=None):
        """Первый элемент после anchor в порядке дерева: кнопка внутри той же карточки."""
        nodes = self.nodes(fresh=False)
        start = next(i for i, node in enumerate(nodes) if node.id == anchor.id)
        labels = set(self.T_all(key)) if key is not None else ({label} if label is not None else None)
        for node in nodes[start + 1:]:
            if (cls is None or node.cls == cls) and (labels is None or node.label in labels):
                return node
        raise LookupError("nothing after %r" % anchor)

    def field(self, key, timeout=8):
        """Поле ввода по ключу его подсказки."""
        labels = set(self.T_all(key))
        deadline = time.time() + timeout
        while True:
            found = [node for node in self.find_all(cls="TextBox") if node.hint in labels]
            if found:
                return found[0]
            if time.time() > deadline:
                raise LookupError("no field: %s" % key)
            time.sleep(0.4)

    def has(self, label=None, key=None, cls=None, match=None):
        return bool(self.find_all(label, key, cls, match=match))

    # нажатия

    def wid(self, win=0):
        title = self.windows[win]["title"] if self.windows else TITLE
        if title not in self.wids:
            found = self.inside("xdotool", "search", "--onlyvisible", "--name", "^%s$" % re.escape(title)).split()
            if not found:
                raise LookupError("no X window: %s" % title)
            self.wids[title] = found[-1]
        return self.wids[title]

    def tap(self, target, wait=1.2, button=1):
        node = target if isinstance(target, Node) else None
        x, y = node.center if node else target
        win = node.win if node else 0
        self.inside("xdotool", "mousemove", "--window", self.wid(win), x, y, "sleep", "0.15", "click", button,
                    "sleep", "0.2", "mousemove", AWAY[0], AWAY[1])
        time.sleep(wait)

    def click(self, label=None, key=None, cls=None, wait=1.2, timeout=8, index=0, match=None):
        node = self.find(label, key, cls, timeout=timeout, index=index, match=match)
        self.tap(node, wait)
        return node

    def hover(self, target, wait=1.5):
        x, y = target.center
        self.inside("xdotool", "mousemove", "--window", self.wid(target.win), x, y)
        time.sleep(wait)

    def wheel(self, target, notches, wait=0.8):
        """Колесо мыши над элементом: notches > 0 листает вниз."""
        x, y = target.center
        button = 5 if notches > 0 else 4
        self.inside("xdotool", "mousemove", "--window", self.wid(target.win), x, y, "click", "--repeat", abs(notches),
                    "--delay", "60", button, "mousemove", AWAY[0], AWAY[1])
        time.sleep(wait)

    def viewer(self, node):
        viewer = node.up("ScrollViewer")
        if viewer is None:
            raise LookupError("not in a scroll viewer: %r" % node)
        return viewer

    def scroll_to(self, viewer, offset, wait=0.8):
        top = max(0, min(offset, (viewer.extent or 0) - (viewer.viewport or 0)))
        self.ask("offset %d %d" % (viewer.id, top))
        time.sleep(wait)

    def main_viewer(self, fresh=True):
        """Самая высокая прокрутка окна: содержимое раздела."""
        viewers = [node for node in self.nodes(fresh) if node.cls == "ScrollViewer" and node.win == 0]
        if not viewers:
            raise LookupError("no scroll viewer")
        return max(viewers, key=lambda node: (node.seen[3] - node.seen[1]) * (node.seen[2] - node.seen[0]))

    def scroll_into(self, find, top, slack=12):
        """Листает прокрутку элемента, который возвращает find(), пока его верх не встанет около top."""
        try:
            node = find()
        except LookupError:
            node = None
        if node is None:
            viewer = self.main_viewer()
            step = max(200, int((viewer.viewport or 400) * 0.8))
            position = 0
            while node is None and position <= (viewer.extent or 0):
                self.scroll_to(viewer, position, wait=0.6)
                try:
                    node = find()
                except LookupError:
                    position += step
                    viewer = self.main_viewer()
            if node is None:
                raise LookupError("not found by scrolling")
        viewer = self.viewer(node)
        delta = node.box[1] - top
        if abs(delta) > slack:
            self.scroll_to(viewer, (viewer.offset or 0) + delta)
            node = find()
        return node

    def type_text(self, text, wait=0.8):
        self.inside("xdotool", "type", "--delay", "40", "--", text)
        time.sleep(wait)

    def key(self, *names, wait=0.8):
        self.inside("xdotool", "key", "--delay", "120", *names)
        time.sleep(wait)

    def enter(self, field, text, wait=1.5):
        """Ставит в поле текст: стирает прежний и печатает новый."""
        self.tap(field, wait=0.5)
        self.key("ctrl+a", "BackSpace", wait=0.3)
        self.type_text(text, wait=wait)

    def set_field(self, key, text, wait=1.5):
        node = self.field(key)
        if node.text == text:
            return node
        self.enter(node, text, wait)
        return self.field(key)

    def start_load(self, seconds=40):
        """Гоняет запросы к youtube через туннель: у подключения скорость, журнал и активные соединения."""
        urls = " ".join(("https://www.youtube.com/", "https://m.youtube.com/", "https://i.ytimg.com/generate_204"))
        loop = ("end=$(( $(date +%%s) + %d )); while [ $(date +%%s) -lt $end ]; do for u in %s; do "
                "curl -s -o /dev/null -m 10 $u; done; done" % (seconds, urls))
        self.load = subprocess.Popen(["bash", STAND, "in", "runuser", "-u", "bor", "--", "bash", "-c", loop],
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def stop_load(self):
        if self.load is not None:
            self.load.kill()
            self.load = None
            for line in self.stand("members").splitlines():
                pid, _, command = line.partition(" ")
                if command.startswith("curl ") or "youtube" in command:
                    subprocess.run(["sudo", "-n", "kill", pid], capture_output=True)

    # приложение

    def restart(self, timeout=40):
        """Окно заново: каждый кадр начинается с главного экрана."""
        self.stand("window", self.locale)
        self.wids = {}
        self.roots = []
        deadline = time.time() + timeout
        while True:
            try:
                self.dump()
                accept = self.find_all(key="Terms_Accept", fresh=False) if self.windows else []
                if accept:
                    # Первый запуск окна на чистой библиотеке: условия принимаются, как это делает пользователь.
                    self.tap(accept[0], wait=2)
                    continue
                if self.windows and self.is_home(fresh=False):
                    break
            except (TimeoutError, OSError, ValueError, RuntimeError):
                pass
            if time.time() > deadline:
                raise RuntimeError("window did not come up")
            time.sleep(0.5)
        wid = self.wid(0)
        if (self.windows[0]["w"], self.windows[0]["h"]) != SIZE:
            self.inside("xdotool", "windowsize", "--sync", wid, SIZE[0], SIZE[1], "windowmove", wid, 40, 40)
            time.sleep(1.5)
            self.dump()

    def home(self):
        self.restart()

    def open_settings(self):
        self.home()
        self.click(key="Main_SettingsTooltip", wait=1.5)

    SUBPAGES = ("BundleExport_WindowTitle", "BundleImport_WindowTitle")

    def open_section(self, key):
        """Открывает раздел настроек по ключу его пункта в меню."""
        self.open_settings()
        self.click(key=key, wait=1.5)
        for title in self.SUBPAGES:
            if self.has(cls="TextBlock", key=title) and not self.has(cls="TextBlock", key=key):
                self.close_page(title)

    def open_editor(self, index=0):
        """Открывает редактор списка маршрутизации шестерёнкой его карточки."""
        self.open_section("Main_RailRoutingTitle")
        self.click(key="Main_CardSettingsLink", cls="Button", wait=2, index=index)

    def open_docs_editor(self, list_id=None):
        """Открывает редактор списка кадров (stand.docs_list) шестерёнкой его карточки."""
        self.open_section("Main_RailRoutingTitle")
        name = self.status_list_name(list_id)
        card = self.scroll_into(lambda: self.find(cls="TextBlock", label=name, timeout=1), 160)
        self.tap(self.after(card, key="Main_CardSettingsLink", cls="Button"), wait=2)

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
        """Закрывает вложенную страницу раздела стрелкой слева от её заголовка."""
        title = self.find(cls="TextBlock", key=title_key)
        arrows = [node for node in self.find_all(cls="Button", fresh=False)
                  if node.seen[2] <= title.seen[0] and node.seen[1] <= title.center[1] <= node.seen[3]]
        self.tap(arrows[-1], wait=1.5)

    def pick(self, combo, labels, wait=1.2):
        """Выбирает пункт выпадающего списка по одной из подписей."""
        labels = [labels] if isinstance(labels, str) else list(labels)
        node = self.find(label=combo) if isinstance(combo, str) else combo
        if node.shown in labels:
            return node.shown
        self.tap(node)
        items = [item for item in self.find_all(label=labels, cls="TextBlock") if item.up("ComboBoxItem") is not None]
        if not items:
            self.key("Escape")
            raise LookupError("no item: %s" % " | ".join(labels))
        self.tap(items[-1], wait)
        return items[-1].label

    def is_home(self, fresh=True):
        nodes = self.nodes(fresh)
        return any(node.cls == "MainView" for node in nodes) and not any(node.cls == "ConnectPill" for node in nodes)

    # кадр

    def mask_boxes(self, extra=(), keep=None):
        """Видимые части элементов главного окна, чей текст подходит под маски."""
        boxes = []
        for node in self.nodes(fresh=False):
            if node.win != 0:
                continue
            label = node.text or node.hint
            if not label or not any(re.search(pattern, label) for pattern in self.masks):
                continue
            if keep and re.search(keep, label):
                continue
            lines = label.split("\n")
            if len(lines) > 1:
                boxes.append((node, lines, keep))
            elif node.visible:
                boxes.append(node.seen)
        boxes.extend(extra)
        return boxes

    def _line_boxes(self, image, node, lines, keep):
        """Строки многострочного текста, подходящие под маски, по полосам текста на снимке; не сошлось - весь текст."""
        left, top, right, bottom = node.seen
        region = image.crop(node.seen).convert("L")
        width, height = region.size
        pixels = region.load()
        background = max(region.getcolors(width * height) or [(0, 255)])[1]
        ink = [any(abs(pixels[x, y] - background) > 60 for x in range(0, width, 2)) for y in range(height)]
        bands = []
        for y, inked in enumerate(ink):
            if inked and bands and y - bands[-1][1] <= 3:
                bands[-1][1] = y
            elif inked:
                bands.append([y, y])
        shift = node.seen[1] - node.box[1]
        text = [line for line in lines if line.strip()]
        if shift or len(bands) != len(text):
            print("line mask: %d bands for %d lines, whole text blurred" % (len(bands), len(text)))
            return [node.seen]
        boxes = []
        for line, (first, last) in zip(text, bands):
            if any(re.search(pattern, line) for pattern in self.masks) and not (keep and re.search(keep, line)):
                boxes.append((left, top + first - 4, right, top + last + 4))
        return boxes

    def _masks(self):
        path = os.path.join(BASE, "local", "mask.txt")
        if not os.path.exists(path):
            return []
        with open(path, encoding="utf-8") as source:
            return [line.strip() for line in source if line.strip() and not line.startswith("#")]

    def capture(self, win=0):
        png = self.inside("import", "-window", self.wid(win), "png:-", binary=True)
        return Image.open(io.BytesIO(png)).convert("RGB")

    def save(self, name, extra=(), wait=1.0, keep=None):
        time.sleep(wait)
        self.dump()
        boxes = self.mask_boxes(extra, keep)
        image = self.capture()
        boxes = [box for item in boxes for box in (self._line_boxes(image, *item) if isinstance(item[0], Node) else [item])]
        for box in boxes:
            region = image.crop(box)
            region = region.filter(ImageFilter.GaussianBlur(14))
            image.paste(region, box[:2])
        folder = os.path.join(BASE, "out", self.lang)
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, name + ".png")
        image.save(path, optimize=True)
        print("saved %s %dx%d masks %d" % (path, image.width, image.height, len(boxes)))
        return path


def tree(ui):
    roots = ui.dump()
    plain = ("ContentPresenter", "Border", "Panel", "Grid", "StackPanel", "DockPanel", "VisualLayerManager")

    def show(node, depth):
        if node.visible and (node.label or node.cls not in plain):
            flags = "".join((" checked" if node.checked else "", " selected" if node.selected else "", " off" if node.off else ""))
            print("%s%s %r %s%s" % ("  " * depth, node.cls, node.label, node.seen, flags))
        for child in node.children:
            show(child, depth + 1)

    for root in roots:
        show(root, 0)


def steps(ui, args):
    """Шаги для проб: k=<ключ> t=<подпись> home esc scroll=<px> sleep=<с> tree shot=<имя> key=<текст>."""
    for arg in args:
        name, _, value = arg.partition("=")
        if name == "k":
            print("tap", ui.click(key=value))
        elif name == "t":
            print("tap", ui.click(label=value))
        elif name == "home":
            ui.home()
        elif name == "esc":
            ui.key("Escape")
        elif name == "scroll":
            viewer = ui.main_viewer()
            ui.scroll_to(viewer, (viewer.offset or 0) + int(value))
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
