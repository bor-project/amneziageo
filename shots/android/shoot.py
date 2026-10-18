# -*- coding: utf-8 -*-
"""Кадры приложения Android на телефоне стенда.

python shoot.py <язык> [кадр ...]  - подготовить телефон, снять кадры (по умолчанию все из frames.txt), вернуть телефон
python shoot.py restore            - вернуть телефон, если съёмка оборвалась
python shoot.py state              - что сейчас на телефоне: язык, тема, выбор, туннель
python shoot.py clean              - удалить свои конфигурации стенда после всех языков
python shoot.py try <язык> <кадр ...> - снять кадры без подготовки и возврата: телефон уже подготовлен к языку
"""
import importlib.util
import os
import sys
import traceback

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "kit"))
import stand  # noqa: E402
from ui import BASE, Ui, languages  # noqa: E402


def frame_names():
    with open(os.path.join(BASE, "frames.txt"), encoding="utf-8") as source:
        return [line.split()[0] for line in source if line.strip() and not line.startswith("#")]


def run_frame(ui, name):
    path = os.path.join(BASE, "frames", name + ".py")
    spec = importlib.util.spec_from_file_location("frame_" + name.replace("-", "_"), path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.run(ui)


def attempt(ui, name):
    print("== %s" % name)
    try:
        run_frame(ui, name)
        return True
    except Exception:
        traceback.print_exc()
        return False


def main(args):
    if not args or args[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if args[0] == "state":
        stand.show(Ui("en"))
        return 0
    if args[0] == "clean":
        ui = Ui("en")
        stand.restore(ui)
        stand.clean(ui)
        return 0
    if args[0] == "try":
        ui = Ui(args[1])
        ui.window_lang = args[1]
        if os.path.exists(stand.STATE):
            stand.resume(ui)
        failed = [name for name in args[2:] if not attempt(ui, name)]
        return 1 if failed else 0
    if args[0] == "restore":
        stand.restore(Ui("en"))
        return 0
    lang = args[0]
    if lang not in languages():
        raise SystemExit("no language %s in languages.json" % lang)
    ui = Ui(lang)
    names = args[1:] or frame_names()
    failed = []
    stand.setup(ui)
    try:
        failed = [name for name in names if not attempt(ui, name)]
    finally:
        stand.restore(ui)
    if failed:
        print("failed: %s" % " ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
