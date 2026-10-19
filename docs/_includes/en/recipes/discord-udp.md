# {{ page.title }}

The simplest way is the ready template: a new list, the "Only selected" mode, the "Unavailable sites" template. It already holds `geosite:discord` - the domains of Discord - and `geoip:ag-discord-voice` - the networks of its voice servers from the project's own database, and "All UDP through the tunnel" is on.

With a list of your own:

1. Create a list and name it `discord`.
2. Add the rules `geosite:discord` and `geoip:ag-discord-voice`. Or instead of them add Discord itself in the application rules: then all its traffic goes through the tunnel, wherever it connects.
{% if page.device == "windows" %}

   {% include shot.html name="routing-discord" alt="The list editor: the geosite:discord entry among the rules of the template" caption="The Discord entry in the list." %}
{% endif %}
3. Turn "All UDP through the tunnel" on: the voice servers of Discord arrive without DNS, and this way voice whose addresses are not in the databases goes into the tunnel too.
4. Since the network cuts UDP, turn WebSocket on in the transport settings: an AmneziaGeo server offers its front itself, for another server set its address, port and authentication if the server requires it.
{% if page.device == "windows" %}

   {% include shot.html name="config-websocket" alt="Configuration page: Proxy (WebSocket) is on" caption="WebSocket in the transport settings. The server address is hidden." %}
{% endif %}
5. Select the `discord` list and connect.

Discord text and voice go through the tunnel, everything else goes direct.

More: [New list](../menu/routing.html#new-list), [Application routing](../menu/routing.html#application-routing), [All UDP through the tunnel](../menu/routing.html#all-udp-through-the-tunnel), [WebSocket](../menu/configurations.html#websocket).
