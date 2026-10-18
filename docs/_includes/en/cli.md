# {{ page.title }}

The core command set is shared across all three platforms: the `AmneziaGeo.Cli` assembly with a thin host per system. Platform-specific commands, such as the Windows multiple-VPN mode, extend it. Everything goes through the agent over the `IpcContract` protocol; the console never touches the database, so the agent and the UI see its edits at once.

## How to run it
{% if page.device == "windows" %}
`amneziageo.exe <command>` from `%ProgramFiles%\AmneziaGeo`.
{% elsif page.device == "linux" %}
`amneziageo <command>`
{% elsif page.device == "android" %}
```text
adb shell am broadcast -a org.amneziageo.android.CLI -n org.amneziageo.android/.CliReceiver --es cmd "<command>"
```

The answer comes back in `data=` of the same `adb` command, is mirrored whole to logcat under the tag `AmneziaGeoCli`, and with `--es out <path>` is written to a file. Pass arguments that start with a dash as an array: `--esa args ops,--probe`. The receiver is gated by `android.permission.DUMP`, which the adb shell holds and an ordinary application cannot get. The UI does not have to be up - the receiver starts the agent in its own process.
{% endif %}

## Day to day

```bash
amneziageo status                   # what runs, and what the next connect would use
amneziageo doctor                   # the checks an install usually trips over
amneziageo runtime                  # the configuration the next connect would use
amneziageo --json config list       # script-friendly output
amneziageo sessions --filter youtube # every address the tunnel decides for, and why
amneziageo log tail --level info
amneziageo tui                      # full-screen console over SSH
```

`amneziageo help` lists every command.
{% if page.device == "windows" %}

## Multiple VPN connections

Use the names of configurations already added to the client. For example, with `office` and `personal`:

```powershell
amneziageo settings set multi-server on
amneziageo fleet up office
amneziageo fleet up personal
amneziageo fleet primary office
amneziageo fleet role personal reserve
amneziageo fleet status
amneziageo fleet down personal
```

To select a server and fallback for an existing proxy rule in a routing list:

```text
amneziageo fleet target <list-id-or-name> <rule> <server-or-auto-best-direct-block> [fallback]
```

For example, after adding `domain:example.com` to the `sites` list with the proxy role:

```powershell
amneziageo fleet target sites domain:example.com personal office
```

`fleet up` adds one connection and `fleet down` removes the named connection. The mode and the `fleet` commands are currently available only on Windows.
{% endif %}
{% if page.device == "linux" %}

## Full-screen console

`amneziageo tui` is the full-screen interface of the console client, over SSH too. Its import box takes a wg-quick configuration, a `vpn://` link or an `http(s)` address.

{% include todo.html text="screens and keys: arrows or Tab move between panels, F10 exits" %}
{% endif %}

## Debugging

```bash
amneziageo log say "run starts"     # mark the agent log from a test script
amneziageo ops                      # protocol operations and the commands that call them
amneziageo ops --probe              # which operations this platform's agent implements
amneziageo ipc <operation> [arg...] # call any operation directly
```

`ops --probe` sends every operation with no arguments: a refusal from its handler means the operation is there, a refusal from the dispatcher means it is not. Operations that would do their work for real with no arguments - connect, download, remove - are marked `-` and never sent.

## Exit codes

| Code | What it means |
|---|---|
| 0 | done |
| 1 | the agent refused |
| 2 | wrong usage |
| 3 | agent unreachable |
| 5 | not implemented by this platform's agent |
