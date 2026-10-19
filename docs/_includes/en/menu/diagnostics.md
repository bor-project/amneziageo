# {{ page.title }}

## Log

"Log" picks what to show: the agent log{% if page.device != "android" %}, the resolver log{% endif %}, the routing log, active connections and the current configuration. The agent log{% if page.device != "android" %} and the resolver log{% endif %} have a "Level" of recording: none, error, warning, info, debug, trace; it is also `amneziageo settings set log-level`. The routing log has the "Log connections" switch: routes and the addresses found for domains reach the log only with it on. "Refresh" pulls new entries in; while Ctrl is held, refreshing pauses. The search finds lines by their text, and "Load earlier" pages back. "Clear" wipes the log, "Copy what is shown" puts the lines on screen on the clipboard, "Export" saves the log to a file. A log line: time · [level] · component · message.
{% if page.device == "windows" %}

{% include shot.html name="diag-agent" alt="The agent log at the info level: checks of the carrying connection and measurements of the link" caption="The agent log. The configuration name and addresses are hidden." %}

{% include shot.html name="diag-routes" alt="The routing log searched for youtube: resolved addresses and the TUNNEL decision by a rule" caption="The routing log, searched for youtube." %}
{% endif %}
{% if page.device == "android" %}
The tunnel process writes a `memory:` line to the routing log once a minute: its size as the system counts it, the managed heap, and what the engine holds in use, free and has given back. After a session comes up it collects in both runtimes and gives the freed memory back to the system, and a stream the relay carries holds a buffer only while it carries bytes.
{% endif %}
Active connections show every address the tunnel decides on, and why: the tunnel, direct or blocked - by a range, by a name, by an application, "all UDP", the address of a domain, from the configuration, a service one or in no rule. "Where" filters by the decision, and the summary counts the addresses: tunnelled, direct, blocked, in no rule. From the console `amneziageo sessions` shows the same.
{% if page.device == "windows" %}

{% include shot.html name="diag-active" alt="Active connections filtered by youtube: the address, the domain, the path and the reason" caption="Active connections, filtered by youtube." %}
{% endif %}

"Collect archive" puts the logs, the settings and the state of the tunnel into one zip archive for support; keys and passwords in it are hidden. From the console: `amneziageo diag collect`.

## Route and speed check

The probe answers the question of where a connection actually went. Give it a domain or an address and a path: **auto** - as the rules decide, **tunnel** or **bypass** - forced.

The report shows the path the traffic took and the rule behind it (`tunnel by rule`, `direct by default`, `blocked by rule`), latency, jitter, loss, the size of packets that get through, and download and upload speed. Speed is measured against a speed service, by default `https://speed.cloudflare.com/__up`; the address is set in the probe settings.
{% if page.device == "windows" %}

{% include shot.html name="diag-probe" alt="Probe of www.youtube.com: the path through the tunnel by a rule, latency, download and upload" caption="The probe: where the connection went and at what speed." %}
{% endif %}

An AmneziaGeo server measures the speed itself. Where it offers that, the probe and the channel check measure against it, inside the tunnel or beside it, and the probe settings say so under the field. An address typed into the field comes first.

### Probe settings

"Data transfer speed test service" - the address the speed is measured against. An empty field means the default: `https://speed.cloudflare.com/__up`, or the AmneziaGeo server of the selected configuration when it offers a measurement.

## Current configuration

The text of the configuration the next connect will take, as the agent put it together: the addresses, the MTU, the resolvers and `AllowedIPs`. It is for reading, not an editor; the tab has no history and refreshes itself.
{% if page.device == "windows" %}

{% include shot.html name="diag-config" alt="The current configuration: the state, routing and transport" caption="The current configuration. The server address is hidden." %}
{% endif %}

From the console `amneziageo runtime` shows the same, see [Command line](../cli.html).
