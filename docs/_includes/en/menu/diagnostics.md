# {{ page.title }}

## Log

{% include todo.html text="Level, Where, Log connections, Refresh, Clear, Copy what is shown, Export, Collect archive" %}
{% if page.device == "android" %}
The tunnel process writes a `memory:` line to the routing log once a minute: its size as the system counts it, the managed heap, and what the engine holds in use, free and has given back. After a session comes up it collects in both runtimes and gives the freed memory back to the system, and a stream the relay carries holds a buffer only while it carries bytes.
{% endif %}

## Route and speed check

The probe answers the question of where a connection actually went. Give it a domain or an address and a path: **auto** - as the rules decide, **tunnel** or **bypass** - forced.

The report shows the path the traffic took and the rule behind it (tunnel by rule, bypass by default, blocked by rule), latency, jitter, loss, the size of packets that get through, and download and upload speed. Speed is measured against a speed service, by default `https://speed.cloudflare.com/__up`; the address is set in the probe settings.

An AmneziaGeo server measures the speed itself. Where it offers that, the probe and the channel check measure against it, inside the tunnel or beside it, and the probe settings say so under the field. An address typed into the field comes first.

### Probe settings

{% include todo.html text="Data transfer speed test service" %}

## Current configuration

{% include todo.html text="what the tab shows" %}

From the console `amneziageo runtime` shows the same, see [Command line](../cli.html).
