---
title: Installation
lang: en
device: windows
topic: install
description: The AmneziaGeo installer for Windows 7, 10 and 11, updates and uninstalling.
---

# Installation

**English** | [Русский](install.ru.md)

You need Windows 7, 10 or 11 (x64 or arm64) and administrator rights: the installer sets up a system service.

1. Download an installer from the [Releases](https://github.com/bor-project/amneziageo/releases) page. The regular build carries everything it needs; the smaller variant requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) already installed.
2. Run it: the service and the app are installed.

Next: [first configuration](first-config.md).

## SmartScreen and Smart App Control

The installers are not signed yet, so SmartScreen warns about an unknown publisher and Smart App Control on Windows 11 may block the launch. What to do about it: [code signing policy](../../CODE_SIGNING.md).

## Update

The app updates itself: it offers the new version and, when the installed copy is the release before it, fetches one patch with the files that changed against that copy, a changed file as a delta from the installed one, and runs the installer on them; from an older version, or where the patch does not fit, it downloads the whole installer for its own architecture. A download that broke off carries on from where it stopped.

## Uninstalling

{% include todo.html text="removing it through Windows Apps; what stays on the disk" %}
