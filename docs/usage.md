# Configuration

**English** | [Русский](usage.ru.md)

## Modes

- **Full tunnel.** Everything goes through the VPN, with the local network still reachable direct. The kill switch cuts internet access if the tunnel drops.
- **Split tunnel.** Traffic goes direct by default, and only what matches the rules takes the VPN. Domains add themselves as soon as a DNS answer arrives for them.

The mode belongs to the routing list, not to the configuration: the same list attaches to different servers.

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

Add a program - a browser, a game, a messenger - and only its traffic takes the tunnel, wherever it connects. The client follows the process and its child processes.

Private addresses are never picked up by such a rule: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, CGNAT and link-local always stay off the tunnel. To send an application into a remote local network, use an address-range rule.

## All UDP through the tunnel

Calls and games usually learn their server addresses without DNS, so a domain rule never catches them. The toggle sends every outgoing UDP datagram into the tunnel, except the local network and the VPN server itself.

## WebSocket transport

AmneziaWG runs over UDP, and on some networks - corporate and guest Wi-Fi, some mobile carriers - it does not get through. The whole tunnel can then run over a WebSocket on TCP: from the outside it looks like ordinary HTTPS traffic.

The front is offered by an AmneziaGeo server: the host of the Endpoint of the configuration at the port of its services. By default that is the port of the Endpoint, or the one in the line `# AmneziaGeo Services = <port>` when the server moved its services. The WebSocket switch is open while the server offers a front, and turning it on stays with you. The front is dialled under TLS, and the server knows the configuration by a token counted from its keys, so the certificate is not checked. From the console: `amneziageo config websocket <name> on|off`.

## What the server offers

The application asks an AmneziaGeo server what it offers a configuration: when the window opens, after a configuration is added or edited, and before every connect. The question goes over TCP to the host of the Endpoint at the port of the services; the application proves the keys of the configuration, and the answer is sealed for it alone. The answer is kept per configuration: the WebSocket front, whether routing on the client is allowed (a ban of the server turns the Routing on the client switch off) and where the speed is measured. A server of another kind, or a silent one, changes nothing: a server that did not answer the last question as an AmneziaGeo server is asked in the background before a connect, and the connect does not wait for it.

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

Traffic reaches the tunnel only while the tunnel is up.

## Route and speed check

The probe answers the question of where a connection actually went. Give it a domain or an address and a path: **auto** - as the rules decide, **tunnel** or **bypass** - forced.

The report shows the path the traffic took and the rule behind it (tunnel by rule, bypass by default, blocked by rule), latency, jitter, loss, the size of packets that get through, and download and upload speed. Speed is measured against a speed service, by default `https://speed.cloudflare.com/__up`; the address is set in the probe settings.

An AmneziaGeo server measures the speed itself. Where it offers that, the probe and the channel check measure against it, inside the tunnel or beside it, and the probe settings say so under the field. An address typed into the field comes first.

## Example: Discord where UDP is blocked

1. Create a list and name it `discord`.
2. In the application rules, pick the running Discord and add it.
3. Turn on all UDP into the tunnel: the voice servers of Discord arrive without DNS.
4. Since the network blocks UDP, enable WebSocket in the transport settings: the switch is open when your AmneziaGeo server offers a front.
5. Select the `discord` list and connect.

Discord text and voice go through the tunnel, everything else goes direct.

## Notes

- The Linux tunnel applies the addresses, MTU and `AllowedIPs` of the configuration itself, the selected routing list, the resolvers stored for the configuration and the WebSocket transport. In split mode it starts with nothing but the resolver routed, and every destination earns its own route on first contact; `route-ttl-seconds` decides how long one outlives its traffic. Rules by application are carried by a cgroup whose mark selects the tunnel; a kernel without unified cgroups leaves those applications on the path of the machine and the agent says so in its log. Exclusions are stored and reported, but not yet enforced.
- Destinations are decided by the names the machine looks up, so an application that resolves on its own over DoH is decided by address alone.
- While the tunnel carries no IPv6, an address over it is withheld from the names the rules send through the tunnel, which would otherwise leave by the physical path.
- The Linux agent runs as root: it creates the tunnel device and rewrites routes, and `CAP_NET_ADMIN` alone does not satisfy its preflight. The control socket is `/tmp/CoreFxPipe_AmneziaGeo.Agent`, so the unit must not set `PrivateTmp`; every local account that can reach the socket can drive the agent and read the keys of a configuration.
