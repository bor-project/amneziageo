---
title: Configuration
lang: en
section: usage
translation: /usage.ru.html
---

# Configuration

**English** | [Русский](usage.ru.md)

## Modes

- **Full tunnel.** Everything goes through the VPN, with the local network still reachable direct. The kill switch cuts internet access if the tunnel drops.
- **Split tunnel.** Traffic goes direct by default, and only what matches the rules takes the VPN. Domains add themselves as soon as a DNS answer arrives for them.

The mode belongs to the routing list, not to the configuration: the same list attaches to different servers.

A fresh install holds no list, and everything goes through the VPN until one is in use: a list the owner adds, or a list a server hands out, see [What the server offers](#what-the-server-offers).

## Multiple VPN connections (Windows)

Windows can keep several VPN tunnels connected at the same time. In the settings, enable the multiple-VPN option, then connect the required server configurations. Connecting another server adds a tunnel while the existing ones stay connected.

- Choose a **main** server and the order of the **reserve** servers. A **neutral** server stays outside that reserve order and carries traffic explicitly assigned to it.
- A routing rule can use **Auto**, **Best**, **Direct**, **Block**, or a named server. You can also select a fallback for that rule.
- This combines split tunneling with several connections: for example, send a work subnet through the office VPN and selected sites through a different VPN, leaving the remaining traffic direct in split-tunnel mode.

This mode is currently Windows-only. Linux and Android keep one active VPN connection. See the [command-line examples](cli.md#multiple-vpn-connections-windows).

## Rule lists

A rule is a kind and a value:

| Kind | What it means |
|---|---|
| `domain:` | a domain and its subdomains |
| `geosite:` | a site category from the shared database |
| `geoip:` | a country |
| `cidr:` | an address range |
| `app:` | an application |

Every rule takes one of three roles: **proxy** - send through the tunnel, **direct** - keep off the tunnel, **block** - refuse. Rules are grouped into lists, and a list is picked for the connection.

Domain and country edits apply on the fly, with no reconnect.

## Application routing

Application routing is experimental, and its UI controls are hidden by default. The `DEBUG` marker next to the UI state enables them after restarting the app; on Windows this is an empty file at `%LOCALAPPDATA%\AmneziaGeo\DEBUG`.

Add a program - a browser, a game, a messenger - and only its traffic takes the tunnel, wherever it connects. The client follows the process and its child processes.

Private addresses are never picked up by such a rule: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, CGNAT and link-local always stay off the tunnel. To send an application into a remote local network, use an address-range rule.

## All UDP through the tunnel

Calls and games usually learn their server addresses without DNS, so a domain rule never catches them. The toggle sends every outgoing UDP datagram into the tunnel, except the local network and the VPN server itself. A list set to "Everything except selected" has no toggle: UDP rides the tunnel there without it.

On Windows the first datagram to an address nothing decided yet is held back until its route into the tunnel is in place, and the repeat rides the tunnel. A program that keeps sending from a socket it connected to the address before that has to open the socket again.

## WebSocket transport

AmneziaWG runs over UDP, and on some networks - corporate and guest Wi-Fi, some mobile carriers - it does not get through. The whole tunnel can then run over a WebSocket on TCP: from the outside it looks like ordinary HTTPS traffic.

An AmneziaGeo server that answered offers its front itself: the host of the Endpoint of the configuration at the port of its services. By default that is the port of the Endpoint, or the one in the line `# AmneziaGeo Services = <port>` when the server moved its services. The front is dialled under TLS, and the server knows the configuration by a token counted from its keys, so the certificate is not checked. A server of ours that offers no front keeps the WebSocket switch closed.

An older AmneziaGeo server names its front in the configuration it hands out, in the line `# AmneziaGeo WebSocket = wss://<host>:<port>/<path>`, and that front is taken as it stands.

For any other server the front is set in the settings: an address and a port, and a login and password or a token if the server requires authentication. An empty address and port take the host and the port of the Endpoint; an address or a port that does not hold counts as empty. Such a front is dialled under TLS with its certificate checked. Turning WebSocket on stays with you. From the console: `amneziageo config websocket <name> on|off [--host <address>] [--port <port>]`.

## What the server offers

The application asks an AmneziaGeo server what it offers a configuration: when the window opens, after a configuration is added or edited, after a subscription or a bundle brings a configuration whose server was not asked yet, and before every connect. The question goes over TCP to the host of the Endpoint at the port of the services; the application proves the keys of the configuration, and the answer is sealed for it alone. The answer is kept per configuration: the WebSocket front and where the speed is measured. The answer also hands out the geo sources of the server and the routing lists the template of the client names. The application adds a source it holds none of at that address, moves a source it holds under the same name to the address of the server, once per address, and fetches these sources at once; a source the owner removed comes back with the next answer. A list of the server is known by its identifier. One the application holds none of is added, and put in use when the server marks it on by default and no list is in use. One it holds stays as it is; when the server changed it since, its card and the title of the open list show Update, and pressing it takes the name, the rules and the traffic switches of the server in place of what the list held, in use or not as it was. A list the owner removed comes back with the next answer. The card of a list names where it came from: the configuration it arrived with, or Local for a list made on the device. A server older than the identifiers has its lists added by name and none of them put in use. A server of another kind, or a silent one, changes nothing. A server that answered the last question as a server of another kind is asked in the background before a connect, and the connect does not wait for it. A server that never answered is not taken for one of another kind: the next three connects wait for it again, three seconds at most each, and after them it is asked in the background too. On Android the question, and the reading of a subscription, leave on the network under the tunnel, so a tunnel that stands dialing does not swallow them; a tunnel that goes on dialing inside a WebSocket has its server asked again with every attempt, and when an answer names another front than the one the tunnel is dialing, the tunnel is dialed again on it. A subscription read at its address is proven by the certificate of its server: by the store of the device, or by the roots of Let's Encrypt the application carries for a device whose store lacks them.

## Leak protection

A tunnel whose server stops answering is not taken down: it stays up while it reconnects on its own, what the rules send through it is held back instead of leaving by the physical path, and what the rules send directly keeps going. The state reads "connecting" until the server answers again, however long that takes. The protection is always on and has no switch.

- A disconnect of yours (the button, the menu of the tray or of the notification, the console) takes the tunnel down, and so does a switch to another configuration on Linux and Windows.
- An AmneziaGeo server takes the tunnel down as well when the client is turned off in its panel: it sends a signal through the tunnel, and the application disconnects the way a disconnect of yours does, so a machine that was reached through the tunnel comes back on its own network. On Windows with several servers the whole set goes down. The application takes the signal on Linux and Windows, on a TCP port the server names, at the address of the tunnel, and lets that port in from the server alone, whatever the access from the tunnel says. On Linux a firewall of the machine itself that drops incoming connections drops the signal too.
- The protection covers a tunnel the server answered at least once after you connected. A connect the server never answered is given up.
- A tunnel whose engine or tunnel service died is raised again at once, and for that moment there is no tunnel. On Android the system setting "Block connections without VPN" closes it.
- On Android the protection keeps the tunnel interface in place for as long as the session runs: an address the rules send directly keeps going through the application until the next connect instead of leaving the interface on its own. A change of the routing list or of the configuration still puts another interface in place, and for that moment a packet can leave by the physical path.
- On Android names are resolved through the tunnel, so while it is held no name resolves; addresses the rules send directly keep working.
- A failure another attempt does not get past ends the hold: the tunnel goes down and says why.
- On Linux and Windows a tunnel that carries everything does not look the name of its server up again while it is held.

## Access to a remote network

Subnets behind the server are reachable in two ways:

- the full tunnel - everything goes to the other side, including its internal addresses;
- an address-range rule in split mode, for example `cidr:10.8.0.0/24` - only that subnet takes the tunnel.

The DNS from the configuration is applied, so internal names resolve.

If the subnet on the other side matches your local one - both `192.168.1.0/24` - no route to it can be built; only re-addressing one of the ends helps.

## Access from the tunnel

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

On Linux the change reaches a running tunnel at once. On Windows the access is collected when the tunnel comes up, so the app raises the reconnect mark and the new state stands from the next connect.

## Proxy for the local network

The app runs SOCKS5 and HTTP proxies that let devices without their own client use the tunnel: a TV, a phone, a console.

- Ports are set separately for SOCKS5 and HTTP.
- Allowing connections from the LAN opens the proxy to the rest of the network; otherwise it serves this machine only.
- Access is by account, or password-free if that suits you better.
- Connected clients and the number of connections they hold are listed.

On Android the list also holds the phone's address on the network it shares itself: a Wi-Fi hotspot, USB or Bluetooth tethering. A device joined to that network takes this address and the HTTP port as the proxy in its Wi-Fi settings: without root Android keeps tethered traffic out of the VPN, and through the proxy it rides the tunnel. The address of the mobile network is not listed: it has no neighbours.

A proxy set in the network settings is followed by the browser and by programs that reach the network through the system. Telegram on a phone is not one of them: the phone's address and the SOCKS5 port go into Telegram itself (Settings - Data and Storage - Proxy Settings, type SOCKS5). The proxy carries TCP connections; datagrams (UDP) of neighbouring devices do not pass through it.

Traffic reaches the tunnel only while the tunnel is up.

## Route and speed check

The probe answers the question of where a connection actually went. Give it a domain or an address and a path: **auto** - as the rules decide, **tunnel** or **bypass** - forced.

The report shows the path the traffic took and the rule behind it (tunnel by rule, bypass by default, blocked by rule), latency, jitter, loss, the size of packets that get through, and download and upload speed. Speed is measured against a speed service, by default `https://speed.cloudflare.com/__up`; the address is set in the probe settings.

An AmneziaGeo server measures the speed itself. Where it offers that, the probe and the channel check measure against it, inside the tunnel or beside it, and the probe settings say so under the field. An address typed into the field comes first.

## Example: Discord where UDP is blocked

1. Create a list and name it `discord`.
2. In the application rules, pick the running Discord and add it.
3. Turn on all UDP into the tunnel: the voice servers of Discord arrive without DNS.
4. Since the network blocks UDP, enable WebSocket in the transport settings: an AmneziaGeo server offers its front itself, for another server set its address, port and authentication if the server requires it.
5. Select the `discord` list and connect.

Discord text and voice go through the tunnel, everything else goes direct.

## Notes

- The Linux tunnel applies the addresses, MTU and `AllowedIPs` of the configuration itself, the selected routing list, the resolvers stored for the configuration and the WebSocket transport. In split mode it starts with nothing but the resolver routed, and every destination earns its own route on first contact; `route-ttl-seconds` decides how long one outlives its traffic. Address ranges do not wait for that contact: whatever the machine opens toward a range a proxy rule names leaves through the tunnel from the first packet, from any socket and over any protocol, a range a block rule names is refused the same way in both modes, and a range a direct rule names goes past a tunnel that carries everything the same way; a rule by name still wins over a range its address lies in. Neither a rule by application nor all UDP carries what a direct rule keeps out, by range or by name. An address a direct rule names that the machine already reaches past the tunnel, a network of its own on another interface or a route laid by other software, keeps that route. In split mode a connection that comes in beside the tunnel from an address of a proxy range is answered the way it came. Rules by application are carried by a cgroup whose mark selects the tunnel; a kernel without unified cgroups leaves those applications on the path of the machine and the agent says so in its log. Exclusions are stored and reported, but not yet enforced.
- Destinations are decided by the names the machine looks up, so an application that resolves on its own over DoH is decided by address alone.
- While the tunnel carries no IPv6, an address over it is withheld from the names the rules send through the tunnel, which would otherwise leave by the physical path.
- The Linux agent runs as root: it creates the tunnel device and rewrites routes, and `CAP_NET_ADMIN` alone does not satisfy its preflight. The control socket is `/tmp/CoreFxPipe_AmneziaGeo.Agent`, so the unit must not set `PrivateTmp`; every local account that can reach the socket can drive the agent and read the keys of a configuration.
- On Linux a dropped tunnel or a failed connect is dialled again 30 s later (`periodic-reconnect-interval-seconds`), and so on until it connects, while auto reconnect is on; it is on unless you turn it off. A tunnel whose server falls silent is repaired in place first, then raised again, and taken down when the server does not answer the new session within 30 s. Between attempts there is no tunnel, so traffic leaves by the physical path unless the leak protection holds a tunnel that stood.
- On Windows a dropped or failed connect is dialled again whatever auto reconnect says: at once, then in 5, 10 and 20 s, then every 60 s. With the setting on, the longest pause is its interval instead of 60 s, and `amneziageo status` says `always` either way.
- On Android a connect stays dialled until the tunnel is up or you take it back: with no network it waits for one, and after a failure it tries again at once, then in 5, 10 and 20 s, then every 60 s. Between attempts there is no tunnel, so traffic leaves by the physical path unless the system setting "Block connections without VPN" is on or the leak protection holds a tunnel that stood.
- On Android a connect to another configuration while a tunnel is connected asks the server of that configuration for a handshake first, past the tunnel. A server that does not answer within 7 s is not switched to: the tunnel stays on the configuration it was on, and the window names the server that did not answer and the configuration the tunnel stays on.
- On Android the application follows the tunnel process: when the system kills it and nobody asked for a disconnect, a window that is on the screen raises the tunnel again within a few seconds. With no window on the screen the tunnel raises itself: while a session is wanted the service keeps pushing an alarm of the system ahead, and once the process is gone the alarm starts it again within a few minutes. A tunnel the alarm has raised three times in a row without a connection stays down, and a "The tunnel has stopped" notification with a "Connect" button says so; that notification has a category of its own, so its sound can be set apart from the rest.
- On Android the notification of the tunnel names the configuration and shows the stage with the speed in both directions, refreshed every 5 seconds while the screen is on, and the attempt while the tunnel is dialled again; unfolded it names the routing list in use. Its button takes the tunnel down, and a tap opens the application. A tunnel you took down leaves a "Disconnected" notification with a "Connect" button, which can be swiped away.
- On Android the tunnel process writes a `memory:` line to the routing log once a minute: its size as the system counts it, the managed heap, and what the engine holds in use, free and has given back. After a session comes up it collects in both runtimes and gives the freed memory back to the system, and a stream the relay carries holds a buffer only while it carries bytes.
- On Android the application picker lists the programs with a launcher icon and every program that asks for network access; system programs without a launcher icon come with the "System" switch, and the search finds them without it. A rule on a program the picker does not list is left as it is.
- On Android the general settings carry a "Background activity" block: notifications, the battery saver and, on MIUI and HyperOS, autostart, each with its state and a button that opens its system screen. `doctor` reports the same three. On MIUI and HyperOS the battery row names no state while the exemption list of Android does not have the application: the system there keeps a setting of its own and manages that list itself.
