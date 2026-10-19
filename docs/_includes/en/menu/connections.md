# {{ page.title }}
{% if page.device == "windows" %}

## Tunnel

- "Show notifications" - tray messages about connecting and about updates of the application and of the databases.
- "Reconnect after reboot" - connect the selected configuration when the computer starts; in the console it is `survive-reboot`.
- "Reconnect automatically" and "Retry interval" - keep trying after a drop: at once, in 5, 10 and 20 s, then every interval; the interval is the longest pause between attempts. How a connect is retried: [Reconnecting](home.html#reconnecting).
- "Allow several VPNs" turns on the mode of the [Multiple VPN connections](../recipes/multiple-vpn.html) recipe. Under it stand the settings of how rules move between servers: "Check interval" - how often to check whether a rule should move to another server, "Checks before a switch" - after how many checks in a row without an answer the rule moves to another server, "Switch threshold" - how much faster the other server has to answer for the rule to move to it.

{% include shot.html name="connections-tunnel" alt="Connections, the Tunnel tab: notifications, reconnect after reboot, reconnecting, several VPNs" caption="The tunnel tab." %}
{% endif %}

## Proxy

The application runs SOCKS5 and HTTP proxies through which devices without a client of their own reach the tunnel: a TV, a phone, a console.

- "Allow connections" turns the proxy on: programs on this computer and devices on the local network use it.
- The ports are set separately: "SOCKS5 port" and "HTTP port". The address at which the proxy is reachable on the network stands beside them, and a button copies it.
- Access is by "Accounts" ("User" and "Password", any of them will do) or through "Connect without a password": then the proxy is open to any device on the network. With no account and without that switch nobody can connect.
- "Clients" shows who is connected and how many connections each holds.
{% if page.device == "windows" %}

{% include shot.html name="connections-proxy" alt="Connections, the Proxy tab: allow connections, addresses, ports, clients and accounts" caption="The proxy is on. Network addresses are hidden." %}
{% endif %}
{% if page.device == "android" %}
The list also holds the phone's address on the network it shares itself: a Wi-Fi hotspot, USB or Bluetooth tethering ("This phone's hotspot"). A device joined to that network takes this address and the HTTP port as the proxy in its Wi-Fi settings: without root Android keeps tethered traffic out of the VPN, and through the proxy it rides the tunnel. The address of the mobile network is not listed: it has no neighbours.
{% endif %}
A proxy set in the network settings is followed by the browser and by programs that reach the network through the system. Telegram on a phone is not one of them: the phone's address and the SOCKS5 port go into Telegram itself (Settings - Data and Storage - Proxy Settings, type SOCKS5). The proxy carries TCP connections; datagrams (UDP) of neighbouring devices do not pass through it.

Traffic reaches the tunnel only while the tunnel is up.

Step by step: the [Devices on the network through the proxy](../recipes/lan-proxy.html) recipe. From the console: `amneziageo proxy show`, `amneziageo proxy on [--socks <port>] [--http <port>] [--anon on|off] [--auth <user:password>]... [--auth off]`, `amneziageo proxy off`.
{% if page.device == "windows" or page.device == "linux" %}

## Access point

The "Wi-Fi" tab. "Access point" turns sharing on: the computer shares Wi-Fi, and the devices joined to it reach the internet through it - with the tunnel up by its rules, so the same goes into the tunnel as for the computer itself, and without the tunnel directly. "Network name" - up to 32 bytes, "Password" - from 8 to 63 characters, and the button beside it shows it. "Band" - "Auto", "2.4 GHz" or "5 GHz"; when the adapter picked the band itself, the line under the field says which one the network runs on. {% if page.device == "linux" %}"Hidden network" takes the name off the air. {% endif %}"Devices" lists the ones joined. The access point has a switch of its own, apart from the proxy.
{% if page.device == "windows" %}
Sharing goes through the Windows service "Internet Connection Sharing": when it is stopped, the tab says so. A Wi-Fi adapter that can share is needed.

{% include shot.html name="connections-hotspot" alt="Connections, the Wi-Fi tab: the access point is on, the network name, the password, the band and the devices joined" caption="The access point is running." %}
{% else %}
The packages `hostapd`, `dnsmasq` and `iw` and a Wi-Fi adapter that can work as an access point are needed; without them the tab names what is missing.
{% endif %}
Step by step: the [Sharing the VPN over Wi-Fi](../recipes/hotspot.html) recipe. From the console: `amneziageo hotspot show`, `amneziageo hotspot on [--ssid <name>] [--password <password>] [--band auto|2.4|5] [--hidden on|off]`, `amneziageo hotspot off`.
{% endif %}

## Subscription auto-refresh

Configurations from subscriptions refresh themselves: the agent reads the subscriptions again every 12 hours unless the panel set a term of its own, and a configuration changed in the panel applies at the next connect. There is no separate setting in the window. To refresh every subscription now, use the "Refresh configs" button in the [configuration](configurations.html#catalogue) catalogue. From the console: `amneziageo settings set subscription-auto-refresh-enabled on|off` turns refreshing on and off, `amneziageo settings set subscription-refresh-interval-hours <1-168>` sets the term in hours; subscriptions are managed with `amneziageo sub list`, `amneziageo sub add <url> [<name>]`, `amneziageo sub refresh [<name>]`, `amneziageo sub remove <name> [--configs]`.
