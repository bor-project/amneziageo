# {{ page.title }}

## Catalogue

{% include todo.html text="configuration cards, In use, Check - checking the servers, Refresh configs - every subscription at once" %}

## Adding a configuration

{% include todo.html text="Add: a .conf file, pasted text or a vpn:// link, an http(s) subscription address, a QR code; a shared settings file - through Import / export in General" %}

## Configuration settings

{% include todo.html text="the name, the configuration text, refreshing a subscription" %}

### Export

{% include todo.html text="QR file, QR link, Subscription; Copy, Download QR, Send…; the QR code contains a private key" %}

### Deleting

{% include todo.html text="Delete configuration and the confirmation" %}

## Transport

{% include todo.html text="MTU (Auto, Config, Manual), IPv6, Routing - use routing where it is available" %}

### WebSocket

AmneziaWG runs over UDP, and on some networks - corporate and guest Wi-Fi, some mobile carriers - it does not get through. The whole tunnel can then run over a WebSocket on TCP: from the outside it looks like ordinary HTTPS traffic.

An AmneziaGeo server that answered offers its front itself: the host of the Endpoint of the configuration at the port of its services. By default that is the port of the Endpoint, or the one in the line `# AmneziaGeo Services = <port>` when the server moved its services. The front is dialled under TLS, and the server knows the configuration by a token counted from its keys, so the certificate is not checked. A server of ours that offers no front keeps the WebSocket switch closed.

An older AmneziaGeo server names its front in the configuration it hands out, in the line `# AmneziaGeo WebSocket = wss://<host>:<port>/<path>`, and that front is taken as it stands.

For any other server the front is set in the settings: an address and a port, and a login and password or a token if the server requires authentication. An empty address and port take the host and the port of the Endpoint; an address or a port that does not hold counts as empty. Such a front is dialled under TLS with its certificate checked. Turning WebSocket on stays with you. From the console: `amneziageo config websocket <name> on|off [--host <address>] [--port <port>]`.

### Hold traffic when the tunnel drops

While the tunnel reconnects, what is selected does not go directly. How it acts: [Holding traffic when the tunnel drops](home.html#holding-traffic-when-the-tunnel-drops).
{% if page.device != "android" %}

### Incoming connections from the VPN network

By default the client answers nothing that arrives from the tunnel: what it opened itself keeps working, and the rest is dropped. Access is granted per configuration and has three states:

| State | Who may reach this machine |
|---|---|
| off | nobody, and that is the default |
| the server | the gateway of the tunnel alone, the first address of the tunnel network |
| the whole tunnel network | every address of the tunnel network |

In the console it is one command, and `config list` prints the state in the INBOUND column:

```bash
amneziageo config inbound <name> off|host|network
amneziageo config list
```

`host` stands for the server, not for this machine: the word names the gateway the configuration dials. In the app the switch sits on the transport tab of the configuration.

The server has to allow it too: a server of AmneziaGeo keeps its own switch per client, and until both sides agree nothing gets through. A port forwarded on the server also needs the client to take what arrives.
{% if page.device == "linux" %}
The change reaches a running tunnel at once.
{% elsif page.device == "windows" %}
The access is collected when the tunnel comes up, so the app raises the reconnect mark and the new state stands from the next connect.
{% endif %}
{% endif %}

### Allow the server to disconnect the tunnel

The server takes the tunnel down when this client is turned off on the server. How it acts: [Holding traffic when the tunnel drops](home.html#holding-traffic-when-the-tunnel-drops).
{% if page.device == "android" %}

### Router

Each connection is checked against the rules, so app and domain rules work. Without it the route table decides, and domains are resolved once at connect.

{% include todo.html text="when the switch shows and what changes for routing lists" %}
{% endif %}

## What the server offers

The application asks an AmneziaGeo server what it offers a configuration: when the window opens, after a configuration is added or edited, after a subscription or a bundle brings a configuration whose server was not asked yet, and before a connect. The first connect of a configuration, and one after its Endpoint or keys changed, waits for the answer; later connects take the kept answer at once, and a server of ours is asked again behind the connect once a day. The question goes over TCP to the host of the Endpoint at the port of the services; the application proves the keys of the configuration, and the answer is sealed for it alone.

The answer is kept per configuration: the WebSocket front and where the speed is measured. The answer also hands out the geo sources of the server and the routing lists the template of the client names. The application adds a source it holds none of at that address, moves a source it holds under the same name to the address of the server, once per address, and fetches these sources at once; a source the owner removed comes back with the next answer.

A list of the server is known by its identifier. One the application holds none of is added, and put in use when the server marks it on by default and no list is in use. One it holds stays as it is; when the server changed it since, its card and the title of the open list show Update, and pressing it takes the name, the rules and the traffic switches of the server in place of what the list held, in use or not as it was. A list the owner removed comes back with the next answer. The card of a list names where it came from: the configuration it arrived with, or Local for a list made on the device. A server older than the identifiers has its lists added by name and none of them put in use.

A server of another kind, or a silent one, changes nothing. A server of ours stays one until it answers as a server of another kind three times in a row, or once when a subscription is refreshed; a port that refuses the connection counts as such an answer. A question that gets no answer (a timeout, a name that does not resolve, a TLS that fails, an error of the server) changes nothing, and the server is asked again after a pause that doubles from a minute up to a day. A server that answered the last question as a server of another kind is asked in the background before a connect, and the connect does not wait for it. A server that never answered is not taken for one of another kind: the next three connects wait for it again, three seconds at most each, and after them it is asked in the background too.
{% if page.device == "android" %}
The question, and the reading of a subscription, leave on the network under the tunnel, so a tunnel that stands dialing does not swallow them; a tunnel that goes on dialing inside a WebSocket has its server asked again with every attempt, and when an answer names another front than the one the tunnel is dialing, the tunnel is dialed again on it.
{% elsif page.device == "windows" %}
A tunnel that stands inside a WebSocket has its server asked again. When the front refuses the tunnel, the question goes at once, and an answer that names another port or path has the tunnel carried there while it stays up. When the tunnel carries nothing, the question goes with every repair, and an answer that offers no front any more has the tunnel raised again without the WebSocket.
{% endif %}
A subscription read at its address is proven by the certificate of its server: by the store of the device, or by the roots of Let's Encrypt the application carries for a device whose store lacks them.
