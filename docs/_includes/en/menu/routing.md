# {{ page.title }}

## Modes

- **Full tunnel.** Everything goes through the VPN, with the local network still reachable direct. The kill switch cuts internet access if the tunnel drops.
- **Split tunnel.** Traffic goes direct by default, and only what matches the rules takes the VPN. Domains add themselves as soon as a DNS answer arrives for them.

The mode belongs to the routing list, not to the configuration: the same list attaches to different servers.

A fresh install holds no list, and everything goes through the VPN until one is in use: a list the owner adds, or a list a server hands out, see [What the server offers](configurations.html#what-the-server-offers).

In the app the mode is called "VPN mode for apps" and is chosen when a list is created: "Only selected" is the split tunnel, the sites and apps of the list connect through the VPN and the rest directly; "Everything except selected" is the full one, everything connects through the VPN except the entries of "Direct" and "Block". The mode of the selected list stands in the heading of the section. On the home screen the "Routing" switch turns the whole list on and off: with it off the tunnel follows the `AllowedIPs` of the configuration.
{% if page.device == "windows" %}

{% include shot.html name="routing-modes" alt="Choosing the VPN mode for apps when a list is created" caption="The mode is chosen when a list is created." %}
{% endif %}

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

"Add" opens "VPN mode for apps" and "Choose a template". "Choose a template" holds the ready templates of the mode, and with them "By hand" - your own rules from scratch - and "Import" - a ready list from a file, the clipboard or a QR code.

For the "Only selected" mode there is one ready template, "Unavailable sites": the sites of a ready list go through the VPN, the rest directly, and "All UDP through the tunnel" is on. For the "Everything except selected" mode there are two: "Everything but the selected region", where "Pick a region" ticks one or more countries whose addresses and sites go directly, and "Everything through VPN", where only the home network stays direct. The default region is the country of the device. The "Block ads" switch adds to any template and closes the domains of the `geosite:category-ads-all` category. After "Add" the list opens in the editor, where the rules can be adjusted.

Import: "Load file", "Paste" from the clipboard or "Scan QR code". A list without a name asks for one before it is saved; a list whose name is taken is saved under a new one.
{% if page.device == "windows" %}

{% include shot.html name="routing-new" alt="New list: choosing a template - Unavailable sites, By hand, Import" caption="A ready template, By hand and Import in one list." %}
{% endif %}

### List card

The card shows the name, the number of rules by role ("N proxy", "N direct", "N blocked"), the "All UDP through the tunnel" label, where the list came from - the name of the configuration it arrived with from the server, or "Local" - and the "Selected" mark on the selected one. The "In use" switch picks the list for connections. A list from a server that the server changed since shows "Update", see [What the server offers](configurations.html#what-the-server-offers). "Settings" opens the rule editor, "Advanced" the traffic settings and the export, and "Delete list" asks for a confirmation; a deleted list cannot be brought back.
{% if page.device == "windows" %}

{% include shot.html name="routing-cards" alt="A routing list card: the name, the mode, the number of rules by role, the All UDP through the tunnel label" caption="A list card." %}
{% endif %}

### Rule editor

At the top stands "List name". "Rule group" switches the basket: "Through VPN", "Direct", "Block", each with a line saying where its traffic goes. "What to add" picks the kind of entry. "Address" - a domain, a `geosite` category, a `geoip` country or an address range on one line, for example `youtube` or `geoip:ru`; while you type, the hints offer "Exact domain match", "Domains in the … category", "IP ranges of …" and "Address range". "Application" adds a program{% if page.device == "android" %} from the application picker{% else %}: "Application" - the file of a program, "Folder" - every program in a folder{% endif %}, see [Application routing](#application-routing). Entries are marked with their kind: "Domain", "GeoSite", "GeoIP", "CIDR", "File", "Folder", "Package", "Service". {% if page.device != "android" %}On a category entry a button shows what it holds and how many entries are there; a{% else %}A{% endif %} category absent from the loaded databases is marked "No entries". The sort button orders the entries by name, "Clear" removes every entry of the list, "Copy" puts the list on the clipboard. Changes take effect after "Save".
{% if page.device == "windows" %}

{% include shot.html name="routing-editor" alt="Rule editor: the list name, the rule group, what to add, the address field and the entries" caption="The rule editor." %}

{% include shot.html name="routing-suggest" alt="Hints while typing netflix: the exact domain, IP ranges and the domains of a category" caption="Hints while typing: a domain, the ranges of a region, a category." %}

{% include shot.html name="routing-category" alt="The geosite:youtube entry unfolded: the number of entries, address ranges and domains" caption="What a category holds and how many entries are there." %}

In the multiple-VPN mode a "Through VPN" entry has "Route" and "Fallback": "Auto", "Best", "Direct", "Block" or the name of a server, see the [Multiple VPN connections](../recipes/multiple-vpn.html) recipe.
{% endif %}

## Application routing

Add a program - a browser, a game, a messenger - and only its traffic takes the tunnel, wherever it connects. The client follows the process and its child processes.

Private addresses are never picked up by such a rule: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, CGNAT and link-local always stay off the tunnel. To send an application into a remote local network, use an address-range rule.

In a full-tunnel list a program is added with the direct role: the tunnel carries the whole machine and the traffic of that program leaves past it. An address the list blocks or sends through the tunnel itself follows the rule of the list for that program too.
{% if page.device == "linux" %}
The program and its child processes leave past the tunnel. A connection opened before the tunnel came up stays silent under it until the program opens it again. An edit of the set of programs applies after a reconnect.
{% elsif page.device == "windows" %}
The way past the tunnel is given to the address the program reached for, not to the program. An address the program looked up by name has its route past the tunnel before the answer reaches the program, so the first attempt goes through; while the list names such a program, the system keeps no answers of the resolver, so every lookup passes the client. The first attempt at an address the program reached without a name lookup is refused, and a second or two later the address has a route past the tunnel. While a route lives (`route-ttl-seconds` after its last use) every program of the machine reaches the address past the tunnel. A UDP socket connected to such an address earlier has to be opened again. A program named by its name, package or service, and a child process whose file lies outside a named folder, is not refused the tunnel: an address it reached without a name lookup goes through the tunnel for the first second or two, then that connection is dropped and opens again past the tunnel. An edit of the set of programs applies without a reconnect.

Shared processes do not fit a rule, and the editor does not take them: for `svchost.exe` pick a particular service from the list, and instead of the shared WebView2 process add the application itself, and its web traffic goes along with it. AmneziaGeo itself is not added to the rules.
{% elsif page.device == "android" %}
A program with the direct role stays out of the tunnel in either mode of the list.

### Picking applications

The application picker lists the programs with a launcher icon and every program that asks for network access; system programs without a launcher icon come with the "System" switch, and the search finds them without it. A rule on a program the picker does not list is left as it is.
{% endif %}

## Advanced
{% if page.device == "windows" %}

{% include shot.html name="routing-advanced" alt="Advanced: all UDP through the tunnel, the route lifetime, system DNS requests, the list export" caption="Advanced settings of a list." %}
{% endif %}

### All UDP through the tunnel

Calls and games usually learn their server addresses without DNS, so a domain rule never catches them. The toggle sends every outgoing UDP datagram into the tunnel, except the local network and the VPN server itself. A list set to "Everything except selected" has no toggle: UDP rides the tunnel there without it.
{% if page.device == "windows" %}
The first datagram to an address nothing decided yet is held back until its route into the tunnel is in place, and the repeat rides the tunnel. A program that keeps sending from a socket it connected to the address before that has to open the socket again.
{% endif %}

### Route lifetime

"How long an address stays cached". An address the rules sent into the tunnel or past it stays on its route for this many seconds after the last use; then the route is taken away and laid again at the next use. 300 by default. The setting is one for all lists. From the console: `amneziageo settings set route-ttl-seconds <seconds>`.
{% if page.device == "windows" %}

### System DNS requests

"Taking address lookups from the system". The client answers the name lookups of the system itself, to see the addresses of the domains of the rules. When lookups stop reaching it over the usual port 53, it takes them over HTTPS. "Automatic" - over HTTPS only once lookups over port 53 stop coming, "On" - over HTTPS at once when the tunnel comes up, "Off" - port 53 alone. From the console: `amneziageo settings set local-doh auto|off|on`.
{% endif %}

### Export list

In "Advanced" the list is exported as "QR" or "Config" - the text of the list. "Copy" puts the text on the clipboard, "Save to file…" writes a file{% if page.device == "android" %}, "Send…" passes it to another app{% endif %}. A long list does not fit into a QR code, and then the text is what remains. On another device the list is taken by "Add" - "Import".

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
