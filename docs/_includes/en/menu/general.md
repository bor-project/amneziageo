# {{ page.title }}

## Appearance

"Theme" - "System", "Light" or "Dark". "Interface language" - "System", "Русский" or "English"; the language of the console is set apart, with the `--lang` key or the `AMNEZIAGEO_LANG` variable.
{% if page.device == "windows" %}

{% include shot.html name="general" alt="The General section: theme, interface language, versions and update" caption="The General section." %}
{% endif %}
{% if page.device == "android" %}

## Background activity

The general settings carry a "Background activity" block: notifications, the battery saver and, on MIUI and HyperOS, autostart, each with its state and a button that opens its system screen. `doctor` reports the same three. On MIUI and HyperOS the battery row names no state while the exemption list of Android does not have the application: the system there keeps a setting of its own and manages that list itself.
{% endif %}

## About

"App version", "Amnezia engine version" - the version of AmneziaWG that comes with the app, "Build type" - the variant of the build when one is set, and "Project repository" - a link to GitHub.

## Update

"Check for updates" asks the release server; a version found is written beside it, "Download" fetches it, "Install" starts the installation, "Cancel" stops the download. "Check for prerelease versions" adds beta builds to the check. The check also runs by itself: a new version is announced by the "Update available" bar at the top of the window{% if page.device == "windows" %} and by a tray notification{% endif %}. The downloaded file is checked against the published checksum.{% if page.device == "android" %} Installing an APK needs the permission to install apps from this source: the system asks for it, and after the answer "Install" has to be pressed again.{% endif %}

How the app updates itself: [Update](../install.html#update) in the installation.
{% if page.device == "windows" %}

## Network repair

When the internet is gone after a failure, the "Repair" button gives the network adapters their former DNS servers back; configurations and routes stay as they are. Administrator rights are needed: Windows asks for a confirmation.
{% endif %}

## Import / export

Moving configurations and routing lists to another device in one file. "Export…" opens the "Export configurations and routing lists" window: tick the configurations and the lists, and in a list the separate rules worth moving (rules for the applications of this computer are of no use on another one), then "Copy content", "Save to file…"{% if page.device == "android" %} or "Send…"{% endif %}. The file carries the keys of the configurations. "Import…" takes such a file: "Choose file…" or a paste from the clipboard, then "On name conflict" - "Add as new", "Replace", "Skip" or "Merge" - and "Save". A file made by a newer version of the application is not read: update the application. From the console: `amneziageo bundle export --all --out <file>` and `amneziageo bundle import --file <file> [--policy new|replace|skip|merge]`.
{% if page.device == "windows" %}

{% include shot.html name="general-export" alt="Export: a configuration and a routing list with its rules ticked" caption="Export: what goes into the file. Server addresses are hidden." %}

{% include shot.html name="general-import" alt="Import: what to do on a name conflict, the field to paste into, the file pick" caption="Import." %}
{% endif %}
