# -*- coding: utf-8 -*-
"""Кадры окна Linux на стенде (запуск на машине стенда).

python3 shoot.py <язык> [кадр ...]    - поднять стенд на язык, снять кадры (по умолчанию все из frames.txt), погасить
python3 shoot.py setup <язык>         - поднять и подготовить стенд к языку и оставить его для try
  --keep                              - поднять на библиотеке прошлого подъёма: без импорта и загрузки баз
python3 shoot.py try <язык> <кадр ...> - снять кадры на поднятом и подготовленном стенде
python3 shoot.py state                - что работает на стенде
python3 shoot.py stop                 - погасить стенд
python3 shoot.py clean                - погасить стенд и стереть библиотеку агента с ключами конфигураций
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
    print("== %s" % name, flush=True)
    try:
        run_frame(ui, name)
        return True
    except Exception:
        traceback.print_exc()
        ui.stop_load()
        return False


def main(args):
    keep = "--keep" in args
    args = [arg for arg in args if arg != "--keep"]
    if not args or args[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if args[0] == "state":
        stand.show(Ui("en"))
        return 0
    if args[0] == "stop":
        stand.restore(Ui("en"))
        return 0
    if args[0] == "clean":
        stand.clean(Ui("en"))
        return 0
    if args[0] == "setup":
        stand.setup(Ui(args[1]), keep)
        return 0
    if args[0] == "try":
        ui = Ui(args[1])
        stand.resume(ui)
        failed = [name for name in args[2:] if not attempt(ui, name)]
        return 1 if failed else 0
    lang = args[0]
    if lang not in languages():
        raise SystemExit("no language %s in languages.json" % lang)
    ui = Ui(lang)
    names = args[1:] or frame_names()
    failed = []
    try:
        stand.setup(ui, keep)
        failed = [name for name in names if not attempt(ui, name)]
    finally:
        stand.restore(ui)
    if failed:
        print("failed: %s" % " ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
