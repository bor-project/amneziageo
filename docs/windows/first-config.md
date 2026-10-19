---
title: First configuration
lang: en
device: windows
topic: first-config
description: Add a configuration, pick the routing and connect.
---

# First configuration

**English** | [Русский](first-config.ru.md)

1. Open AmneziaGeo and import a configuration - a `.conf` file, a QR code or a shared settings file.
2. Pick a rule list or the full tunnel and connect.

## Add a configuration

While there are no configurations, the home screen shows the "Add configuration" button, and later "Add" in the "Configurations" section. The ways: "Load file" - a `.conf` file or a picture with a QR code, "Paste" - the text of a configuration, a `vpn://` link or a subscription address from the clipboard, "Create manually" - wg-quick text in the editor. A file can also be dragged into the window. A shared settings file from another device - configurations and routing lists together - is imported through "Import / export" in the "General" section.

{% include shot.html name="config-add" alt="The Add menu in the catalogue: load file, paste, scan QR code, create manually" caption="Ways to add a configuration." %}

The configuration catalogue: [Configurations](menu/configurations.md).

## Pick the routing

While there is no list, all traffic goes through the VPN. A list is created in the "Routing" section with the "Add" button: pick the mode - "Only selected" or "Everything except selected" - and a template, for example "Unavailable sites", and turn "In use" on for the list. An AmneziaGeo server sends its lists itself: such a list appears in the section and, when the server marked it on by default, is selected while no other is.

{% include shot.html name="routing-new" alt="New list: the VPN mode for apps and the template choice" caption="New list: the mode and the template." %}

Modes and lists: [Routing](menu/routing.md).

## Connect

Click the button in the middle of the home screen. While the connection is being established, the line under the button reads "Establishing connection…", and for a connected tunnel "Connected"; a failed attempt is named by its cause: the server did not answer, the configuration could not be read and so on. Once connected, the window can be closed: the application stays in the tray, and the menu of its icon connects, disconnects and opens the window.

{% include shot.html name="home-connected" alt="Home screen: the tunnel is connected" caption="The tunnel is connected." %}

More: [Home screen](menu/home.md).

## What next

- [Menu](menu/index.md)
- [Recipes](recipes/index.md)
