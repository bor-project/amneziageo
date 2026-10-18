# {{ page.title }}

Проще всего готовым шаблоном: список «Вручную», режим «Только выбранное», шаблон «Недоступные сайты». В нём уже есть `geosite:discord` - домены Discord - и `geoip:ag-discord-voice` - сети его голосовых серверов из собственной базы проекта, а «Весь UDP через туннель» включён.

Своим списком:

1. Создайте список и назовите его `discord`.
2. Добавьте правила `geosite:discord` и `geoip:ag-discord-voice`. Или вместо них добавьте в правилах приложений сам Discord: тогда через туннель пойдёт весь его трафик, куда бы он ни обращался.
{% if page.device == "windows" %}

   {% include shot.html name="routing-discord" alt="Редактор списка: запись geosite:discord среди правил шаблона" caption="Запись Discord в списке." %}
{% elsif page.device == "android" %}

   {% include shot.html name="routing-discord" alt="Редактор списка: записи geosite:discord и geoip:ag-discord-voice среди правил шаблона" caption="Записи Discord в списке." %}
{% elsif page.device == "linux" %}

   {% include shot.html name="routing-discord" alt="Редактор списка: записи geosite:discord и geoip:ag-discord-voice среди правил шаблона" caption="Записи Discord в списке." %}
{% endif %}
3. Включите «Весь UDP через туннель»: голосовые серверы Discord приходят не через DNS, и так в туннель уйдёт и голос, адреса которого в базах нет.
4. Раз в сети режут UDP, в настройках транспорта включите WebSocket: сервер AmneziaGeo предлагает вход сам, для другого сервера укажите его адрес, порт и авторизацию, если сервер её требует.
{% if page.device == "windows" %}

   {% include shot.html name="config-websocket" alt="Страница конфигурации: «Прокси» (WebSocket) включён" caption="WebSocket в настройках транспорта. Адрес сервера скрыт." %}
{% elsif page.device == "android" %}

   {% include shot.html name="config-websocket" alt="Страница конфигурации: «Прокси» (WebSocket) включён, изменение ещё не сохранено" caption="WebSocket в настройках транспорта." %}
{% elsif page.device == "linux" %}

   {% include shot.html name="config-websocket" alt="Страница конфигурации: «Прокси» (WebSocket) включён, изменение ещё не сохранено" caption="WebSocket в настройках транспорта." %}
{% endif %}
5. Выберите список `discord` и подключитесь.

Текст и голос Discord идут через туннель, остальное напрямую.

Подробнее: [Новый список](../menu/routing.ru.html#новый-список), [Маршрутизация приложений](../menu/routing.ru.html#маршрутизация-приложений), [Весь UDP через туннель](../menu/routing.ru.html#весь-udp-через-туннель), [WebSocket](../menu/configurations.ru.html#websocket).
