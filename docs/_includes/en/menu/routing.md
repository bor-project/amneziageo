# {{ page.title }}

## Modes

- **Full tunnel.** Everything goes through the VPN, with the local network still reachable direct. The kill switch cuts internet access if the tunnel drops.
- **Split tunnel.** Traffic goes direct by default, and only what matches the rules takes the VPN. Domains add themselves as soon as a DNS answer arrives for them.

The mode belongs to the routing list, not to the configuration: the same list attaches to different servers.

A fresh install holds no list, and everything goes through the VPN until one is in use: a list the owner adds, or a list a server hands out, see [What the server offers](configurations.html#what-the-server-offers).

{% include todo.html text="the modes in the app: Everything except selected and Only selected; the routing switch on the home screen" %}

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

The databases for `geosite:` and `geoip:` are in [Geo sources](geo-sources.html).

### New list

{% include todo.html text="Add: VPN mode for apps, Choose a template - Region and Block ads; Load file, Paste, Scan QR code" %}

### List card

{% include todo.html text="In use, Update on a list from a server, where the list came from - the configuration name or Local; Delete list" %}

### Rule editor

{% include todo.html text="List name, rule groups, adding by Address, Application and Folder; sorting, Clear, Copy" %}

## Application routing

Add a program - a browser, a game, a messenger - and only its traffic takes the tunnel, wherever it connects. The client follows the process and its child processes.

Private addresses are never picked up by such a rule: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, CGNAT and link-local always stay off the tunnel. To send an application into a remote local network, use an address-range rule.

In a full-tunnel list a program is added with the direct role: the tunnel carries the whole machine and the traffic of that program leaves past it. An address the list blocks or sends through the tunnel itself follows the rule of the list for that program too.
{% if page.device == "linux" %}
The program and its child processes leave past the tunnel. A connection opened before the tunnel came up stays silent under it until the program opens it again. An edit of the set of programs applies after a reconnect.
{% elsif page.device == "windows" %}
The way past the tunnel is given to the address the program reached for, not to the program. An address the program looked up by name has its route past the tunnel before the answer reaches the program, so the first attempt goes through; while the list names such a program, the system keeps no answers of the resolver, so every lookup passes the client. The first attempt at an address the program reached without a name lookup is refused, and a second or two later the address has a route past the tunnel. While a route lives (`route-ttl-seconds` after its last use) every program of the machine reaches the address past the tunnel. A UDP socket connected to such an address earlier has to be opened again. A program named by its name, package or service, and a child process whose file lies outside a named folder, is not refused the tunnel: an address it reached without a name lookup goes through the tunnel for the first second or two, then that connection is dropped and opens again past the tunnel. An edit of the set of programs applies without a reconnect.
{% elsif page.device == "android" %}
A program with the direct role stays out of the tunnel in either mode of the list.

### Picking applications

The application picker lists the programs with a launcher icon and every program that asks for network access; system programs without a launcher icon come with the "System" switch, and the search finds them without it. A rule on a program the picker does not list is left as it is.
{% endif %}

## Advanced

### All UDP through the tunnel

Calls and games usually learn their server addresses without DNS, so a domain rule never catches them. The toggle sends every outgoing UDP datagram into the tunnel, except the local network and the VPN server itself. A list set to "Everything except selected" has no toggle: UDP rides the tunnel there without it.
{% if page.device == "windows" %}
The first datagram to an address nothing decided yet is held back until its route into the tunnel is in place, and the repeat rides the tunnel. A program that keeps sending from a socket it connected to the address before that has to open the socket again.
{% endif %}

### Route lifetime

{% include todo.html text="How long an address stays cached, `route-ttl-seconds`" %}

### System DNS requests

{% include todo.html text="Taking address lookups from the system: Automatic, Off, On" %}

### Export list

{% include todo.html text="Config and QR; Copy, Save to file…, Send…" %}

## Limitations

- Destinations are decided by the names the machine looks up, so an application that resolves on its own over DoH is decided by address alone.
- While the tunnel carries no IPv6, an address over it is withheld from the names the rules send through the tunnel, which would otherwise leave by the physical path.
{% if page.device == "linux" %}

## How Linux applies the rules

The Linux tunnel applies the addresses, MTU and `AllowedIPs` of the configuration itself, the selected routing list, the resolvers stored for the configuration and the WebSocket transport.

In split mode it starts with nothing but the resolver routed, and every destination earns its own route on first contact; `route-ttl-seconds` decides how long one outlives its traffic. Address ranges do not wait for that contact: whatever the machine opens toward a range a proxy rule names leaves through the tunnel from the first packet, from any socket and over any protocol, a range a block rule names is refused the same way in both modes, and a range a direct rule names goes past a tunnel that carries everything the same way; a rule by name still wins over a range its address lies in.

Neither a rule by application nor all UDP carries what a direct rule keeps out, by range or by name. An address a direct rule names that the machine already reaches past the tunnel, a network of its own on another interface or a route laid by other software, keeps that route. In split mode a connection that comes in beside the tunnel from an address of a proxy range is answered the way it came.

Rules by application are carried by a cgroup whose mark selects the tunnel; a kernel without unified cgroups leaves those applications on the path of the machine and the agent says so in its log. Exclusions are stored and reported, but not yet enforced.
{% endif %}
