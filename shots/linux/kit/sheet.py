# -*- coding: utf-8 -*-
"""Листы для просмотра: python3 kit/sheet.py <язык> [кадр ...] кладёт кадры по шесть в половинном размере
в out/_sheet<N>.png с подписями."""
import os
import sys

from PIL import Image, ImageDraw

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main(lang, names):
    folder = os.path.join(BASE, "out", lang)
    names = names or sorted(name[:-4] for name in os.listdir(folder) if name.endswith(".png"))
    for page in range(0, len(names), 6):
        chunk = names[page:page + 6]
        sheet = Image.new("RGB", (2 * 492 + 30, 3 * 396 + 40), "#808080")
        draw = ImageDraw.Draw(sheet)
        for index, name in enumerate(chunk):
            image = Image.open(os.path.join(folder, name + ".png")).convert("RGB")
            image = image.resize((image.width // 2, image.height // 2), Image.LANCZOS)
            x = 10 + (index % 2) * 502
            y = 10 + (index // 2) * 406
            draw.text((x, y), name, fill="white")
            sheet.paste(image, (x, y + 14))
        path = os.path.join(BASE, "out", "_sheet%d.png" % (page // 6 + 1))
        sheet.save(path)
        print(path, " ".join(chunk))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])
