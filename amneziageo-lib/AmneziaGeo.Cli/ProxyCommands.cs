using System.Globalization;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Local proxy the agent offers to this machine and, when it is allowed, to the local network.
/// </summary>
internal static class ProxyCommands
{
    private const string Usage =
        "usage: amneziageo proxy [show|on|off] [--socks <port>] [--http <port>] [--lan on|off] [--auth <user:password>|off]";

    /// <summary>
    /// Runs one proxy command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] == "show")
        {
            return Show(agent);
        }

        return args[0] switch
        {
            "on" => await SetAsync(agent, true, [.. args.Skip(1)]).ConfigureAwait(false),
            "off" => await SetAsync(agent, false, [.. args.Skip(1)]).ConfigureAwait(false),
            _ => Reply.Usage(Usage),
        };
    }

    private static int Show(IAgentLink agent)
    {
        var snapshot = agent.Snapshot;
        var host = snapshot.ProxyAddress.Length > 0 ? snapshot.ProxyAddress : "127.0.0.1";
        var values = new (string Key, string Value)[]
        {
            ("state", State(snapshot)),
            ("socks5", $"{host}:{snapshot.ProxySocksPort.ToString(CultureInfo.InvariantCulture)}"),
            ("http", $"{host}:{snapshot.ProxyHttpPort.ToString(CultureInfo.InvariantCulture)}"),
            ("lan", snapshot.ProxyLan ? "on" : "off"),
            ("user", snapshot.ProxyUser.Length > 0 ? snapshot.ProxyUser : "-"),
            ("password", snapshot.ProxyPassword.Length > 0 ? snapshot.ProxyPassword : "-"),
        };

        if (Output.Json)
        {
            Output.AsJson(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            return Exit.Ok;
        }

        Output.Pairs(values);
        if (snapshot.ProxyEnabled && snapshot.ProxyLan && snapshot.ProxyUser.Length == 0)
        {
            Output.Info(string.Empty);
            Output.Info("every machine on this network can use the proxy; set --auth to ask for a password.");
        }

        return Exit.Ok;
    }

    private static string State(StatusSnapshot snapshot)
    {
        if (!snapshot.ProxyEnabled)
        {
            return "off";
        }

        if (snapshot.ProxyError.Length > 0)
        {
            return $"failed: {snapshot.ProxyError}";
        }

        return snapshot.ProxyRunning ? "listening" : "starting";
    }

    // The switch goes last, so the listener comes up on the settings this command names.
    private static async Task<int> SetAsync(IAgentLink agent, bool on, IReadOnlyList<string> args)
    {
        var updates = new List<(string Key, string Value)>();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--socks" or "--http":
                {
                    var key = args[i] == "--socks" ? SettingKeys.ProxySocksPort : SettingKeys.ProxyHttpPort;
                    if (!Next(args, ref i, out var raw) || !SettingKeys.TryParseProxyPort(raw, out var port))
                    {
                        return Reply.Usage($"{key} takes a port between 1 and 65535");
                    }

                    updates.Add((key, port.ToString(CultureInfo.InvariantCulture)));
                    break;
                }

                case "--lan":
                {
                    if (!Next(args, ref i, out var raw) || !Toggle.TryParse(raw, out var allow))
                    {
                        return Reply.Usage("--lan takes on or off");
                    }

                    updates.Add((SettingKeys.ProxyLan, Toggle.Text(allow)));
                    break;
                }

                case "--auth":
                {
                    if (!Next(args, ref i, out var raw))
                    {
                        return Reply.Usage("--auth takes user:password or off");
                    }

                    if (raw == "off")
                    {
                        updates.Add((SettingKeys.ProxyUser, string.Empty));
                        updates.Add((SettingKeys.ProxyPassword, string.Empty));
                        break;
                    }

                    var colon = raw.IndexOf(':');
                    if (colon <= 0 || colon == raw.Length - 1)
                    {
                        return Reply.Usage("--auth takes user:password or off");
                    }

                    updates.Add((SettingKeys.ProxyUser, raw[..colon]));
                    updates.Add((SettingKeys.ProxyPassword, raw[(colon + 1)..]));
                    break;
                }

                default:
                    return Reply.Usage(Usage);
            }
        }

        updates.Add((SettingKeys.ProxyEnabled, Toggle.Text(on)));
        foreach (var (key, value) in updates)
        {
            var ack = await agent.SendAsync(IpcContract.OpSetSetting, key, value).ConfigureAwait(false);
            if (!ack.Ok)
            {
                return Reply.Report(ack);
            }
        }

        return Reply.Report(new IpcAck(true, string.Empty), on ? "the local proxy is on" : "the local proxy is off");
    }

    private static bool Next(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
