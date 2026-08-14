using System.Globalization;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Local proxy the agent offers to this machine and to the local network.
/// </summary>
internal static class ProxyCommands
{
    private const string Usage =
        "usage: amneziageo proxy [show|on|off] [--socks <port>] [--http <port>] [--anon on|off] [--auth <user:password>]... [--auth off]";

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
        var accounts = ProxyCredentials.Parse(snapshot.ProxyCredentials);
        var values = new (string Key, string Value)[]
        {
            ("state", State(snapshot)),
            ("socks5", Endpoints(snapshot, snapshot.ProxySocksPort)),
            ("http", Endpoints(snapshot, snapshot.ProxyHttpPort)),
            ("anonymous", snapshot.ProxyAnonymous ? "on" : "off"),
            ("accounts", accounts.Count > 0 ? string.Join(", ", accounts.Select(a => $"{a.User}:{a.Password}")) : "-"),
            ("clients", Clients(snapshot)),
        };

        if (Output.Json)
        {
            Output.AsJson(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            return Exit.Ok;
        }

        Output.Pairs(values);
        Warn(snapshot, accounts.Count);
        return Exit.Ok;
    }

    // What the settings let through: everyone on this network, or nobody at all.
    private static void Warn(StatusSnapshot snapshot, int accounts)
    {
        if (!snapshot.ProxyEnabled)
        {
            return;
        }

        if (snapshot.ProxyAnonymous)
        {
            Output.Info(string.Empty);
            Output.Info("every machine on this network can use the proxy; set --auth to ask for a password.");
            return;
        }

        if (accounts == 0)
        {
            Output.Info(string.Empty);
            Output.Info("no account is set, so nobody is admitted; add --auth user:password or --anon on.");
        }
    }

    // The addresses of this machine; loopback only where it has none.
    private static string Endpoints(StatusSnapshot snapshot, int port)
    {
        var hosts = snapshot.ProxyAddresses ?? [];
        if (hosts.Count == 0)
        {
            hosts = ["127.0.0.1"];
        }

        return string.Join(", ", hosts.Select(host => $"{host}:{port.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static string Clients(StatusSnapshot snapshot)
    {
        var clients = snapshot.ProxyClients ?? [];
        if (clients.Count == 0)
        {
            return "-";
        }

        return string.Join(", ", clients.Select(client =>
            $"{client.Address} x{client.Connections.ToString(CultureInfo.InvariantCulture)}"
            + (client.Name.Length > 0 ? $" ({client.Name})" : string.Empty)));
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
        var accounts = new List<ProxyAccount>();
        var authGiven = false;
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

                case "--anon":
                {
                    if (!Next(args, ref i, out var raw) || !Toggle.TryParse(raw, out var allow))
                    {
                        return Reply.Usage("--anon takes on or off");
                    }

                    updates.Add((SettingKeys.ProxyAnonymous, Toggle.Text(allow)));
                    break;
                }

                case "--auth":
                {
                    if (!Next(args, ref i, out var raw))
                    {
                        return Reply.Usage("--auth takes user:password or off");
                    }

                    authGiven = true;
                    if (raw == "off")
                    {
                        accounts.Clear();
                        break;
                    }

                    var colon = raw.IndexOf(':');
                    if (colon <= 0 || colon == raw.Length - 1)
                    {
                        return Reply.Usage("--auth takes user:password or off");
                    }

                    accounts.Add(new ProxyAccount(raw[..colon], raw[(colon + 1)..]));
                    break;
                }

                default:
                    return Reply.Usage(Usage);
            }
        }

        // The accounts named replace the stored ones whole; a command that names none leaves them alone.
        if (authGiven)
        {
            updates.Add((SettingKeys.ProxyCredentials, ProxyCredentials.Compose(accounts)));
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
