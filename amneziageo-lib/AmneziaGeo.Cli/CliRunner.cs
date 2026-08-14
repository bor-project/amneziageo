using System.Globalization;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Cli;

/// <summary>
/// The console itself: parses the command line, reaches the agent and runs one command.
/// </summary>
public static class CliRunner
{
    /// <summary>
    /// Seconds a command waits for its reply when --timeout is absent.
    /// </summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>
    /// Runs one command line and returns the process exit code.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, ICliHost host, CancellationToken ct)
    {
        var globals = GlobalOptions.Split(args);
        if (globals.Error is { } parseError)
        {
            Output.Error(parseError);
            return Exit.Usage;
        }

        Loc.Instance.ApplyStartupCulture(globals.Value("lang") ?? Environment.GetEnvironmentVariable("AMNEZIAGEO_LANG"));
        Output.Json = globals.Has("json");
        Output.Quiet = globals.Has("quiet");
        TextInput.StandardInput = host.StandardInput;

        var rest = globals.Rest;
        if (globals.Has("version") || (rest.Count > 0 && rest[0] == "version"))
        {
            return await VersionAsync(host, ct).ConfigureAwait(false);
        }

        if (globals.Has("help") || globals.Has("h") || rest.Count == 0 || rest[0] == "help")
        {
            Output.Line(Usage(host));
            return rest.Count == 0 && !globals.Has("help") && !globals.Has("h") ? Exit.Usage : Exit.Ok;
        }

        if (host.TryRunLocalAsync(rest, ct) is { } local)
        {
            return await local.ConfigureAwait(false);
        }

        var timeout = TimeSpan.FromSeconds(Seconds(globals.Value("timeout"), DefaultTimeoutSeconds));
        var wait = TimeSpan.FromSeconds(Seconds(Environment.GetEnvironmentVariable("AMNEZIAGEO_CONNECT_WAIT"), 5));
        var agent = await host.ConnectAsync(timeout, wait, ct).ConfigureAwait(false);
        try
        {
            if (agent is null)
            {
                Output.Error(host.UnreachableHint());
                return Exit.Unreachable;
            }

            if (host.TryRunWithAgentAsync(agent, rest, ct) is { } platform)
            {
                return await platform.ConfigureAwait(false);
            }

            return await DispatchAsync(agent, host, rest, ct).ConfigureAwait(false);
        }
        finally
        {
            (agent as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The help text, with the platform's own commands folded in.
    /// </summary>
    public static string Usage(ICliHost host)
    {
        var extra = host.ExtraUsage.Length > 0 ? host.ExtraUsage.TrimEnd() + "\n\n" : string.Empty;
        return $"""
            {host.ExeName} - console client of the AmneziaGeo agent.

            usage: {host.ExeName} [global options] <group> <command> [arguments]

            global options
              --json              print machine-readable JSON instead of tables
              --lang en|ru        output language (default: $AMNEZIAGEO_LANG, else the system language)
              --timeout <sec>     how long to wait for a reply (default: {DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}, geo downloads need more)
              --quiet             print only what was asked for
              --help, -h          this help
              --version           client and agent version

            connection
              status                            what the agent runs and what it would run
              watch                             follow status changes until interrupted
              select <config>                   choose what the next connect binds to
              up [<config>]                     connect (optionally selecting first)
              down                              disconnect

            configurations
              config list
              config show <name>                print the stored wg-quick text
              config link <name>                print the config as a vpn:// share link
              config import <name> (--file <path> | --link <url> | --text <s> | --stdin)
              config edit <name> (--file <path> | --text <s> | --stdin)
              config rename <name> <new-name>
              config copy <name> <new-name>
              config remove <name>
              config dns <name> [<servers>]     preferred resolvers; omit to clear
              config exclusions <name> (--file <path> | --stdin | --list a,b,c | --clear)
              config websocket <name> on|off [--host <h>] [--port <n>] [--mtu <n>] [--ipv6 on|off]
              config geo <name> on|off [rule...]

            routing lists
              routing list
              routing use <id|name|none>
              routing show <id|name>
              routing create <name> [rule...]
              routing set <id|name> [rule...]       replace every rule
              routing add <id|name> <rule...>       append rules
              routing delete-rule <id|name> <rule...>
              routing remove <id|name>
              routing settings <id|name>
              routing configure <id|name> [--exclusions <a,b>] [--exclusions-file <p>]
                                          [--all-udp on|off] [--global-proxy on|off]

            A rule is "<role>|<token>" where role is proxy, direct or block, and token is
            geosite:<category>, geoip:<country>, domain:<name>, app:<id>, or an address/CIDR.
            A bare token without a role is treated as proxy.

            geo databases
              geo list [--filter <text>]        categories the loaded bases expose
              geo show <rule> [--limit <n>]     what a category expands to
              geo update [<source>]             re-download the sources and rebuild the lists
              geo check [<source>]              ask whether a newer file exists, without downloading
              geo download                      seed the default sources and download them
              source list
              source add geosite|geoip <url>
              source edit <name> geosite|geoip <url>
              source remove <name>

            agent settings
              settings show
              settings set <key> <value>
              Known keys: log-level (error|warning|info|debug|trace), route-log (on|off),
              survive-reboot (on|off, connect at agent start), periodic-reconnect-enabled (on|off),
              periodic-reconnect-interval-seconds (5..3600), route-ttl-seconds.

            local proxy
              proxy show                        where it listens and what it asks for
              proxy on [--socks <port>] [--http <port>] [--anon on|off] [--auth <user:password>|off]
              proxy off

            logs and diagnostics
              log tail [--table ageo|routes|checks] [--limit <n>] [--level <token>] [--search <text>]
              log follow [--table ...] [--level ...] [--search ...] [--interval <sec>]
              log clear [--table ageo|routes|checks]
              log export [--table ageo|routes|checks] [--out <path>]
              log say <text>                    mark the agent log from a test script
              runtime                           the configuration the next connect would use
              cache [--filter <text>]           resolutions, routes and addresses the agent holds
              sessions                          where the traffic is going right now
              subnets                           local subnets, ready to paste into exclusions
              apps [--filter <text>]            what per-app rules can address here
              doctor                            check the things a headless install gets wrong
              check                             measure the channel leg by leg and name the culprit
              check channel [host]              the same, timed against a destination you name
              check <target>                    why a domain, address, app: or geo rule goes where it goes
              diag collect                      write a redacted support bundle and print its path
              update check                      ask whether a newer application exists

            portable bundles
              bundle export [--all] [--config <n>]... [--list <n>]... [--out <path>]
              bundle import (--file <path> | --stdin) [--policy new|replace|skip|merge]

            the contract itself
              ops [--probe]                     operations of the protocol; --probe asks this agent
                                                which of them it implements
              ipc <op> [arg...]                 send any operation verbatim and print its reply

            {extra}environment
              AMNEZIAGEO_LANG             output language when --lang is absent
              AMNEZIAGEO_CONNECT_WAIT     seconds to wait for the agent to answer (default: 5)

            Exit codes: 0 done, 1 the agent refused, 2 wrong usage, 3 agent unreachable,
            5 not implemented by this agent.
            """;
    }

    private static async Task<int> DispatchAsync(IAgentLink agent, ICliHost host, IReadOnlyList<string> rest, CancellationToken ct)
    {
        var arguments = (IReadOnlyList<string>)[.. rest.Skip(1)];
        return rest[0] switch
        {
            "status" or "watch" or "select" or "up" or "down" => await StatusCommands.RunAsync(agent, rest[0], arguments, ct).ConfigureAwait(false),
            "config" => await ConfigCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "routing" => await RoutingCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "geo" or "source" => await GeoCommands.RunAsync(agent, rest[0], arguments).ConfigureAwait(false),
            "settings" => await SettingsCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "proxy" => await ProxyCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "log" or "runtime" or "cache" or "sessions" or "subnets" or "doctor" or "diag" or "check" => await DiagCommands.RunAsync(agent, host, rest[0], arguments, ct).ConfigureAwait(false),
            "bundle" => await BundleCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "ipc" or "ops" or "apps" or "update" => await OpsCommands.RunAsync(agent, rest[0], arguments).ConfigureAwait(false),
            _ => Reply.Usage($"unknown command '{rest[0]}'; run '{host.ExeName} help'"),
        };
    }

    // The client version is known without the agent; the agent's own is worth a short dial.
    private static async Task<int> VersionAsync(ICliHost host, CancellationToken ct)
    {
        var client = typeof(CliRunner).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var link = await host.ConnectAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        try
        {
            var agent = link?.Snapshot.AgentVersion ?? "(agent unreachable)";
            if (Output.Json)
            {
                Output.AsJson(new { client, agent });
                return Exit.Ok;
            }

            Output.Pairs([("client", client), ("agent", agent)]);
            return Exit.Ok;
        }
        finally
        {
            (link as IDisposable)?.Dispose();
        }
    }

    private static int Seconds(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;
}
