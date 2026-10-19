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

1. Download the installer for your architecture from the [Releases](https://github.com/bor-project/amneziageo/releases) page. The regular build carries everything it needs; the smaller variant (`-fdd`) requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) already installed. The x64 installer does install on an ARM64 computer, but the tunnel will not work there; the installer warns about it.
2. Run it. "Installation options": "Desktop shortcut", "Start menu shortcut", "Download geo database updates" - fetch the geo databases that are missing or out of date, "Launch AmneziaGeo after install" and "Connect on launch". The agent service and the application are installed: the files go to `Program Files\AmneziaGeo`, the data to `ProgramData\AmneziaGeo`.

{% include shot.html name="install-options" width="504" height="502" alt="Update options: geo databases, launch after install, connecting, shortcuts, reset settings" caption="The options of an update; a new installation shows the same boxes." %}

Next: [first configuration](first-config.md).

Running the installer again over the installed version offers "Update", "Repair" or "Remove"; the "Reset settings" box then wipes the configurations and the data, see [Uninstalling](#uninstalling). An earlier version is installed as a rollback: the installer removes the installed one first, then installs its own.

{% include shot.html name="install-actions" width="504" height="502" alt="The installer over the installed version: Remove, Repair, Update" caption="Running the installer again." %}

## SmartScreen and Smart App Control

The installer and the files of the application are not signed yet: the work on getting a certificate is under way. Until then SmartScreen warns about an unknown publisher and Smart App Control on Windows 11 may block the launch. What to do about it: [code signing policy](../../CODE_SIGNING.md).

<!--
The installer and the files of the application are signed through SignPath.io with a SignPath Foundation certificate, see the [code signing policy](../../CODE_SIGNING.md). Releases before 1.5.x carry no signature: for them SmartScreen warns about an unknown publisher, and Smart App Control on Windows 11 may block the launch.
-->

## Update

The app updates itself: it offers the new version and, when the installed copy is the release before it, fetches one patch with the files that changed against that copy, a changed file as a delta from the installed one, and runs the installer on them; from an older version, or where the patch does not fit, it downloads the whole installer for its own architecture. A download that broke off carries on from where it stopped. The buttons are in the [General](menu/general.md#update) section.

## Uninstalling

It is removed from Windows "Apps" ("Add or remove programs") or by running the installer again and pressing "Remove". The agent service is stopped and taken away, and the application in `Program Files\AmneziaGeo`, the shortcuts and the temporary tunnel services are removed. The data - the state database, the configurations, the geo databases and the logs in `ProgramData\AmneziaGeo` - stays so that a new installation picks it up; the "Delete configuration and cache" box in the removal options wipes it too.

{% include shot.html name="install-remove" width="504" height="502" alt="Removal: the Delete configuration and cache box" caption="Removal: the configurations and the cache stay unless the box is ticked." %}
