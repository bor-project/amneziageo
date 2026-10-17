# {{ page.title }}

## Appearance

{% include todo.html text="Theme, Interface language" %}
{% if page.device == "android" %}

## Background activity

The general settings carry a "Background activity" block: notifications, the battery saver and, on MIUI and HyperOS, autostart, each with its state and a button that opens its system screen. `doctor` reports the same three. On MIUI and HyperOS the battery row names no state while the exemption list of Android does not have the application: the system there keeps a setting of its own and manages that list itself.
{% endif %}

## About

{% include todo.html text="App version, Amnezia engine version, Build type, Project repository" %}

## Update

{% include todo.html text="Check for prerelease versions, Check for updates, Download, Install" %}

How the app updates itself: [Update](../install.html#update) in the installation.
{% if page.device == "windows" %}

## Network repair

{% include todo.html text="“If the internet is gone, restores the network adapters' original DNS servers. Configurations and routes stay as they are.”" %}
{% endif %}

## Import / export

{% include todo.html text="“Transfer configurations and routing lists to another device”: Export…, Import…" %}
