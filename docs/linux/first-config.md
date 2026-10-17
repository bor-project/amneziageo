---
title: First configuration
lang: en
device: linux
topic: first-config
description: Add a configuration, pick the routing and connect - from the console or in the window.
---

# First configuration

**English** | [Русский](first-config.ru.md)

## From the console

```bash
sudo amneziageo geo download
sudo amneziageo config import work --file work.conf
sudo amneziageo up work
sudo amneziageo settings set survive-reboot on
```

Import also takes `--link` with a `vpn://` URL or `--stdin`. An `http(s)` address in `--link` is downloaded: the agent reads it as a subscription, whether it serves a list of links or a single configuration, and keeps it up to date. The import box of `amneziageo tui` takes the same three kinds of text.

`up <config>` selects the configuration and connects on it; `select <config>` only remembers it for the next connect. The routing list is one setting for the whole machine, not a per-configuration pairing: `routing use <name>` picks it, `routing use none` leaves the configuration's own `AllowedIPs` to decide what the tunnel carries.

`survive-reboot` starts the agent at boot and dials at its start; without it a reboot leaves the server without a tunnel. A tunnel that dies is dialled again by itself: `periodic-reconnect-enabled` is on unless you turn it off.

## In the window

{% include todo.html text="on a desktop - the same as on Windows: Add configuration, the routing list, connecting" %}

The configuration catalogue: [Configurations](menu/configurations.md); modes and lists: [Routing](menu/routing.md).

## What next

- [Menu](menu/index.md)
- [Recipes](recipes/index.md)
- [Command line](cli.md)
