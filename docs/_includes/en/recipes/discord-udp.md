# {{ page.title }}

1. Create a list and name it `discord`.
2. In the application rules, pick the running Discord and add it.
3. Turn on all UDP into the tunnel: the voice servers of Discord arrive without DNS.
4. Since the network blocks UDP, enable WebSocket in the transport settings: an AmneziaGeo server offers its front itself, for another server set its address, port and authentication if the server requires it.
5. Select the `discord` list and connect.

Discord text and voice go through the tunnel, everything else goes direct.

More: [Application routing](../menu/routing.html#application-routing), [All UDP through the tunnel](../menu/routing.html#all-udp-through-the-tunnel), [WebSocket](../menu/configurations.html#websocket).
