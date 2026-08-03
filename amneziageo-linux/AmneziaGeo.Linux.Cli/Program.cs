using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Console client of the AmneziaGeo agent.
/// </summary>
public static class Program
{
    private const int PrSetPtracer = 0x59616d61;
    private const string _usage = """
        amneziageo - console client of the AmneziaGeo agent.

        usage: amneziageo [global options] <group> <command> [arguments]

        global options
          --json              print machine-readable JSON instead of tables
          --lang en|ru        output language (default: $AMNEZIAGEO_LANG, else the system language)
          --timeout <sec>     how long to wait for a reply (default: 60, geo downloads need more)
          --quiet             print only what was asked for
          --help, -h          this help
          --version           client version

        connection
          status                            what the agent runs and what it would run
          watch                             follow status changes until Ctrl+C
          select <profile|config>           choose what the next connect binds to
          up [<profile|config>]             connect (optionally selecting first)
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

        profiles
          profile list
          profile add <name> [<config>]
          profile rename <name> <new-name>
          profile remove <name>
          profile routing <name> <list-id|list-name|none> [on|off]

        routing lists
          routing list
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
        geosite:<category>, geoip:<country>, domain:<name>, or an address/CIDR.
        A bare token without a role is treated as proxy.

        geo databases
          geo list [--filter <text>]        categories the loaded bases expose
          geo show <rule> [--limit <n>]     what a category expands to
          geo update [<source>]             re-download the sources and rebuild the lists
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
          periodic-reconnect-interval-seconds (5..3600).

        logs and diagnostics
          log tail [--table ageo|routes] [--limit <n>] [--level <token>] [--search <text>]
          log follow [--table ...] [--level ...] [--search ...] [--interval <sec>]
          log clear [--table ageo|routes]
          log export [--table ageo|routes] [--out <path>]
          runtime                           the configuration the next connect would use
          cache [--filter <text>]           resolutions, routes and addresses the agent holds
          subnets                           local subnets, ready to paste into exclusions
          doctor                            check the things a headless install gets wrong

        portable bundles
          bundle export [--all] [--profile <n>]... [--config <n>]... [--list <n>]... [--out <path>]
          bundle import (--file <path> | --stdin) [--policy new|replace|skip|merge]

        service
          daemon install [--data <dir>] [--iface <name>] [--engine <path>] [--print]
          daemon uninstall | start | stop | restart | status | logs [--follow]

        full-screen console
          tui                               menu-driven configuration over SSH

        environment
          AMNEZIAGEO_LANG             output language when --lang is absent
          AMNEZIAGEO_CONNECT_WAIT     seconds to wait for the agent to answer (default: 5)
          AMNEZIAGEO_WAIT_DEBUGGER    on: pause at start until a debugger attaches

        Exit codes: 0 done, 1 the agent refused, 2 wrong usage, 3 agent unreachable,
        5 not implemented by the Linux agent.
        """;

    private static async Task<int> Main(string[] args)
    {
        WaitForDebugger();

        var globals = GlobalOptions.Split(args);
        if (globals.Error is { } parseError)
        {
            Output.Error(parseError);
            return Exit.Usage;
        }

        Loc.Instance.ApplyStartupCulture(globals.Value("lang") ?? Environment.GetEnvironmentVariable("AMNEZIAGEO_LANG"));
        Output.Json = globals.Has("json");
        Output.Quiet = globals.Has("quiet");

        var rest = globals.Rest;
        if (globals.Has("help") || globals.Has("h") || rest.Count == 0 || rest[0] == "help")
        {
            Output.Line(_usage);
            return rest.Count == 0 && !globals.Has("help") && !globals.Has("h") ? Exit.Usage : Exit.Ok;
        }

        if (globals.Has("version") || rest[0] == "version")
        {
            return await VersionAsync(globals).ConfigureAwait(false);
        }

        if (rest[0] == "daemon")
        {
            return await DaemonCommands.RunAsync([.. rest.Skip(1)]).ConfigureAwait(false);
        }

        return await WithAgentAsync(globals, rest).ConfigureAwait(false);
    }

    private static async Task<int> WithAgentAsync(GlobalOptions globals, IReadOnlyList<string> rest)
    {
        var timeout = TimeSpan.FromSeconds(Seconds(globals.Value("timeout"), 60));
        using var agent = new AgentClient(timeout);
        if (!await agent.ConnectAsync(ConnectWait(5)).ConfigureAwait(false))
        {
            Output.Error(AgentClient.UnreachableHint());
            return Exit.Unreachable;
        }

        var arguments = (IReadOnlyList<string>)[.. rest.Skip(1)];
        return rest[0] switch
        {
            "status" or "watch" or "select" or "up" or "down" => await StatusCommands.RunAsync(agent, rest[0], arguments).ConfigureAwait(false),
            "config" => await ConfigCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "profile" => await ProfileCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "routing" => await RoutingCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "geo" or "source" => await GeoCommands.RunAsync(agent, rest[0], arguments).ConfigureAwait(false),
            "settings" => await SettingsCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "log" or "runtime" or "cache" or "subnets" or "doctor" => await DiagCommands.RunAsync(agent, rest[0], arguments).ConfigureAwait(false),
            "bundle" => await BundleCommands.RunAsync(agent, arguments).ConfigureAwait(false),
            "tui" => await Tui.TuiApp.RunAsync(agent).ConfigureAwait(false),
            _ => Unknown(rest[0]),
        };
    }

    private static async Task<int> VersionAsync(GlobalOptions globals)
    {
        var client = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        using var agent = new AgentClient(TimeSpan.FromSeconds(Seconds(globals.Value("timeout"), 60)));
        var reachable = await agent.ConnectAsync(ConnectWait(2)).ConfigureAwait(false);
        var agentVersion = reachable ? agent.Snapshot.AgentVersion : "(agent unreachable)";

        if (Output.Json)
        {
            Output.AsJson(new { client, agent = agentVersion });
            return Exit.Ok;
        }

        Output.Pairs([("client", client), ("agent", agentVersion)]);
        return Exit.Ok;
    }

    private static int Unknown(string group)
    {
        Output.Error($"unknown command '{group}'; run 'amneziageo help'");
        return Exit.Usage;
    }

    private static void WaitForDebugger()
    {
        var requested = Environment.GetEnvironmentVariable("AMNEZIAGEO_WAIT_DEBUGGER");
        if (requested is not ("1" or "on" or "true"))
        {
            return;
        }

        // Yama allows ptrace only from a parent; the debugger starts beside us.
        _ = prctl(PrSetPtracer, nuint.MaxValue, 0, 0, 0);

        Console.Error.WriteLine($"waiting for a debugger, pid {Environment.ProcessId}");
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!Debugger.IsAttached && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }

        Console.Error.WriteLine(Debugger.IsAttached ? "debugger attached" : "no debugger came, running anyway");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    private static TimeSpan ConnectWait(int fallback) =>
        TimeSpan.FromSeconds(Seconds(Environment.GetEnvironmentVariable("AMNEZIAGEO_CONNECT_WAIT"), fallback));

    private static int Seconds(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;
}
