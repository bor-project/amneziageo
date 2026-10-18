"""Где: linux/recipes/discord-udp
Что видно: редактор списка из шаблона «Недоступные сайты» на записи geosite:discord
Состояние: список из текущего шаблона; ничего не меняется
"""


def run(ui):
    ui.open_docs_editor(ui.template_list)
    ui.scroll_into(lambda: ui.find(cls="TextBlock", label="geosite:discord", timeout=1), 300)
    print("ag-discord-voice in list:", ui.has(cls="TextBlock", label="geoip:ag-discord-voice"))
    ui.save("routing-discord")
