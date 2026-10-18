"""Где: linux/recipes/discord-udp, menu/configurations
Что видно: страница конфигурации, «Прокси» (WebSocket) включён, изменение ещё не сохранено
Состояние: подключённая конфигурация (у неё служебный порт сервера, и сервер сам предлагает вход по WebSocket);
переключатель возвращается «Отменой»
"""
import stand


def run(ui):
    ui.open_section("Main_RailConfigTitle")
    name = ui.find(cls="TextBlock", label=stand.MAIN_CONFIG)
    ui.tap(ui.after(name, key="Main_CardSettingsLink"), wait=2)
    hint = ui.find(cls="TextBlock", key="Main_ProxyWebSocketHint")
    ui.tap(ui.after(hint, cls="ToggleSwitch"), wait=2)
    ui.save("config-websocket")
    ui.click(key="Main_CancelButton", wait=1.5)
