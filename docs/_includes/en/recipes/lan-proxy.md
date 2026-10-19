# {{ page.title }}

{% if page.device == "android" %}1. In the "Connections" section turn "Allow connections" on.{% else %}1. In the "Connections" section, on the "Proxy" tab, turn "Allow connections" on.{% endif %}
2. Set the access: add an account ("User" and "Password") or turn "Connect without a password" on. Without either nobody connects, and without a password the proxy is open to any device on the network.
3. Change the "SOCKS5 port" and the "HTTP port" if you need to, then save.
4. Note the address of the machine on the network: it stands beside the ports, and a button copies it.
{% if page.device == "windows" %}

   {% include shot.html name="connections-proxy" alt="Connections, the Proxy tab: allow connections, addresses, ports, clients and accounts" caption="The proxy is on. Network addresses are hidden." %}
{% endif %}
5. On the device enter the proxy in the settings of its Wi-Fi network: the address of the machine and the HTTP port. A TV, a console and a phone take it from there; Telegram in its own settings: Settings - Data and Storage - Proxy Settings, type SOCKS5, the SOCKS5 port.
6. Bring the tunnel up: the proxy works only while it is connected.
{% if page.device == "android" %}

## Sharing from the phone

{% include todo.html text="a Wi-Fi hotspot, USB or Bluetooth tethering: on the joined device, enter the phone's address on the shared network and the HTTP port as the proxy of its Wi-Fi network" %}
{% endif %}

More: [Proxy](../menu/connections.html#proxy).
