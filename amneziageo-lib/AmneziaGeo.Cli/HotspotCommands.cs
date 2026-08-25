using System.Globalization;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Access point the agent raises so a device joins it instead of being pointed at the proxy.
/// </summary>
internal static class HotspotCommands
{
    private const string Usage =
        "usage: amneziageo hotspot [show|on|off] [--ssid <name>] [--password <password>] [--band auto|2.4|5]";

    /// <summary>
    /// Runs one access point command.
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
        var values = new (string Key, string Value)[]
        {
            ("state", State(snapshot)),
            ("sharing", snapshot.ShareMode),
            ("ssid", snapshot.HotspotSsid.Length > 0 ? snapshot.HotspotSsid : "-"),
            ("password", snapshot.HotspotPassword.Length > 0 ? snapshot.HotspotPassword : "-"),
            ("band", Band(snapshot)),
            ("clients", Clients(snapshot)),
        };

        if (Output.Json)
        {
            Output.AsJson(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            return Exit.Ok;
        }

        Output.Pairs(values);
        Warn(snapshot);
        return Exit.Ok;
    }

    // What keeps the point from standing, in the words that name the remedy.
    private static void Warn(StatusSnapshot snapshot)
    {
        var line = snapshot.HotspotReason switch
        {
            HotspotReasons.NoAdapter => "no wireless adapter on this machine.",
            HotspotReasons.RadioOff => "the wireless adapter is switched off.",
            HotspotReasons.NoApMode => "this adapter does not run as an access point.",
            HotspotReasons.NoTools => "hostapd and dnsmasq are not installed.",
            HotspotReasons.ServiceOff => "the Internet Connection Sharing service is stopped.",
            HotspotReasons.NoPlatform => "this system carries no access point.",
            _ => string.Empty,
        };

        if (line.Length > 0)
        {
            Output.Info(string.Empty);
            Output.Info(line);
            return;
        }

        if (ShareModes.CarriesWifi(snapshot.ShareMode)
            && !(SettingKeys.IsValidHotspotSsid(snapshot.HotspotSsid) && SettingKeys.IsValidHotspotPassword(snapshot.HotspotPassword)))
        {
            Output.Info(string.Empty);
            Output.Info("set --ssid and --password, and the access point comes up.");
        }
    }

    private static string Clients(StatusSnapshot snapshot)
    {
        return snapshot.HotspotRunning
            ? $"{snapshot.HotspotClients.ToString(CultureInfo.InvariantCulture)} of {snapshot.HotspotMaxClients.ToString(CultureInfo.InvariantCulture)}"
            : "-";
    }

    // The band in force, named only where the adapter did not take the one asked for.
    private static string Band(StatusSnapshot snapshot)
    {
        var wanted = HotspotBands.Of(snapshot.HotspotBand);
        var actual = snapshot.HotspotBandActual;
        if (actual.Length == 0 || string.Equals(actual, wanted, StringComparison.Ordinal))
        {
            return wanted;
        }

        return $"{wanted} ({actual} in force)";
    }

    private static string State(StatusSnapshot snapshot)
    {
        if (!ShareModes.CarriesWifi(snapshot.ShareMode))
        {
            return "off";
        }

        if (snapshot.HotspotError.Length > 0)
        {
            return $"failed: {snapshot.HotspotError}";
        }

        return snapshot.HotspotRunning ? "up" : "starting";
    }

    // The mode goes last, so the point comes up on the name and the password this command names.
    private static async Task<int> SetAsync(IAgentLink agent, bool on, IReadOnlyList<string> args)
    {
        var updates = new List<(string Key, string Value)>();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--ssid":
                {
                    if (!Next(args, ref i, out var ssid) || !SettingKeys.IsValidHotspotSsid(ssid))
                    {
                        return Reply.Usage($"--ssid takes 1 to {SettingKeys.HotspotSsidMaxBytes.ToString(CultureInfo.InvariantCulture)} bytes");
                    }

                    updates.Add((SettingKeys.HotspotSsid, ssid));
                    break;
                }

                case "--password":
                {
                    if (!Next(args, ref i, out var password) || !SettingKeys.IsValidHotspotPassword(password))
                    {
                        return Reply.Usage(
                            $"--password takes {SettingKeys.HotspotPasswordMinLength.ToString(CultureInfo.InvariantCulture)} to "
                            + $"{SettingKeys.HotspotPasswordMaxLength.ToString(CultureInfo.InvariantCulture)} characters");
                    }

                    updates.Add((SettingKeys.HotspotPassword, password));
                    break;
                }

                case "--band":
                {
                    if (!Next(args, ref i, out var band) || !HotspotBands.IsKnown(band))
                    {
                        return Reply.Usage("--band takes auto, 2.4 or 5");
                    }

                    updates.Add((SettingKeys.HotspotBand, HotspotBands.Of(band)));
                    break;
                }

                default:
                    return Reply.Usage(Usage);
            }
        }

        updates.Add((SettingKeys.ShareMode, Mode(agent.Snapshot.ShareMode, on)));
        foreach (var (key, value) in updates)
        {
            var ack = await agent.SendAsync(IpcContract.OpSetSetting, key, value).ConfigureAwait(false);
            if (!ack.Ok)
            {
                return Reply.Report(ack);
            }
        }

        return Reply.Report(new IpcAck(true, string.Empty), on ? "the access point is on" : "the access point is off");
    }

    // The switch adds the access point to the ways in force or takes it out, leaving the proxy where it was.
    private static string Mode(string current, bool on)
    {
        if (on)
        {
            return ShareModes.CarriesWifi(current) ? ShareModes.Of(current) : ShareModes.Both;
        }

        return ShareModes.Lan;
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
