"""Где: android/recipes/discord-udp, menu/configurations
Что видно: страница конфигурации, «Прокси» (WebSocket) включён, изменение ещё не сохранено
Состояние: вторая своя конфигурация, не подключённая; переключатель возвращается «Отменой»
"""
import stand


def run(ui):
    ui.open_section("Main_RailConfigTitle")
    name = ui.scroll_into(lambda: ui.find(cls="TextBlock", label=stand.STAND_CONFIGS[1], timeout=1), 560)
    ui.tap(ui.after(name, key="Main_CardSettingsLink", cls="Button"), wait=2.5)
    ui.scroll(-3000)
    hint = ui.find(cls="TextBlock", key="Main_ProxyWebSocketHint")
    ui.tap(ui.after(hint, cls="ToggleSwitch"), wait=3)
    ui.save("config-websocket")
    ui.click(key="Main_CancelButton", cls="Button", wait=2)
