"""Где: android/recipes/discord-udp
Что видно: редактор списка из шаблона «Недоступные сайты» на записи geosite:discord
Состояние: свой список из текущего шаблона (у владельца список из старого шаблона, без discord); ничего не меняется
"""


def run(ui):
    ui.open_docs_editor(ui.template_list)
    ui.scroll_into(lambda: ui.find(cls="TextBlock", label="geosite:discord", timeout=1), 1100)
    print("ag-discord-voice in list:", ui.has(cls="TextBlock", label="geoip:ag-discord-voice"))
    ui.save("routing-discord")
