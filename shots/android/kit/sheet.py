# -*- coding: utf-8 -*-
"""Листы просмотра: python kit/sheet.py <язык> <кадр ...> - кадры рядом по три в out/_sheet.png."""
import os
import sys

from PIL import Image

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
lang, names = sys.argv[1], sys.argv[2:]
images = [Image.open(os.path.join(BASE, "out", lang, name + ".png")) for name in names]
columns = min(3, len(images))
rows = (len(images) + columns - 1) // columns
sheet = Image.new("RGB", (370 * columns, 734 * rows), "white")
for index, image in enumerate(images):
    sheet.paste(image.resize((360, 724)), ((index % columns) * 370, (index // columns) * 734))
sheet.save(os.path.join(BASE, "out", "_sheet.png"))
