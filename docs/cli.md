# Command line

**English** | [Русский](cli.ru.md)

One command set on all three platforms: the shared `AmneziaGeo.Cli` assembly with a thin host per system. Everything goes through the agent over the `IpcContract` protocol; the console never touches the database, so the agent and the UI see its edits at once.

| Platform | How to run it |
|---|---|
| Linux | `amneziageo <command>` |
| Windows | `amneziageo.exe <command>` from `%ProgramFiles%\AmneziaGeo` |
| Android | `adb shell am broadcast -a org.amneziageo.android.CLI -n org.amneziageo.android/.CliReceiver --es cmd "<command>"` |

On Android the answer comes back in `data=` of the same `adb` command, is mirrored whole to logcat under the tag `AmneziaGeoCli`, and with `--es out <path>` is written to a file. Pass arguments that start with a dash as an array: `--esa args ops,--probe`. The receiver is gated by `android.permission.DUMP`, which the adb shell holds and an ordinary application cannot get. The UI does not have to be up - the receiver starts the agent in its own process.

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
