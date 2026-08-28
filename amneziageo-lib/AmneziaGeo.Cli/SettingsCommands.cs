using System.Globalization;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Agent settings the headless install depends on.
/// </summary>
internal static class SettingsCommands
{
    private const string _logLevelKey = "log-level";
    private const string _routeLogKey = "route-log";
    private const string _surviveRebootKey = "survive-reboot";
    private const string _periodicReconnectKey = "periodic-reconnect-enabled";
    private const string _reconnectIntervalKey = "periodic-reconnect-interval-seconds";

    private static readonly string[] _levels = ["error", "warning", "info", "debug", "trace"];

    /// <summary>
    /// Runs one settings command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo settings <show|set>");
        }

        return args[0] switch
        {
            "show" => Show(agent),
            "set" => await SetAsync(agent, [.. args.Skip(1)]).ConfigureAwait(false),
            _ => Reply.Usage($"unknown settings command '{args[0]}'"),
        };
    }

    private static int Show(IAgentLink agent)
    {
        var snapshot = agent.Snapshot;
        var values = new (string Key, string Value)[]
        {
            (_logLevelKey, snapshot.LogLevel),
            (_routeLogKey, snapshot.RouteLog ? "on" : "off"),
            (_surviveRebootKey, snapshot.SurviveReboot ? "on" : "off"),
            (_periodicReconnectKey, snapshot.PeriodicReconnect ? "on" : "off"),
            (_reconnectIntervalKey, snapshot.PeriodicReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture)),
            (SettingKeys.RouteTtl, snapshot.RouteTtlSeconds.ToString(CultureInfo.InvariantCulture)),
            (SettingKeys.SubscriptionAutoRefresh, snapshot.SubscriptionAutoRefresh ? "on" : "off"),
            (SettingKeys.SubscriptionRefreshInterval, snapshot.SubscriptionRefreshIntervalHours.ToString(CultureInfo.InvariantCulture)),
        };

        if (Output.Json)
        {
            Output.AsJson(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            return Exit.Ok;
        }

        Output.Pairs(values);
        Output.Info(string.Empty);
        Output.Info("Only the keys above ride the status snapshot; anything else set-setting stores is write-only.");
        return Exit.Ok;
    }

    private static async Task<int> SetAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            return Reply.Usage("usage: amneziageo settings set <key> <value>");
        }

        var (key, raw) = (args[0], args[1]);
        if (!TryNormalize(key, raw, out var value, out var error))
        {
            return Reply.Usage(error);
        }

        var ack = await agent.SendAsync(IpcContract.OpSetSetting, key, value).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        if (key == _surviveRebootKey && value == "on" && !agent.Snapshot.PeriodicReconnect)
        {
            Output.Info("the agent will connect at start; turn periodic-reconnect-enabled on as well so a dropped tunnel comes back");
        }

        return Reply.Report(ack, $"{key} = {value}");
    }

    // The agent takes only its own spellings and silently coerces the rest, so normalize before sending.
    private static bool TryNormalize(string key, string raw, out string value, out string error)
    {
        value = raw;
        error = string.Empty;
        switch (key)
        {
            case _logLevelKey:
                value = raw.Trim().ToLowerInvariant();
                if (!_levels.Contains(value))
                {
                    error = $"{key} takes one of {string.Join(", ", _levels)}";
                    return false;
                }

                return true;

            case _routeLogKey or _surviveRebootKey or _periodicReconnectKey or SettingKeys.SubscriptionAutoRefresh:
                if (!Toggle.TryParse(raw, out var on))
                {
                    error = $"{key} takes on or off";
                    return false;
                }

                value = Toggle.Text(on);
                return true;

            case _reconnectIntervalKey:
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds is < 5 or > 3600)
                {
                    error = $"{key} takes whole seconds between 5 and 3600";
                    return false;
                }

                value = seconds.ToString(CultureInfo.InvariantCulture);
                return true;

            case SettingKeys.SubscriptionRefreshInterval:
                if (!SettingKeys.TryParseSubscriptionInterval(raw, out var hours))
                {
                    error = $"{key} takes whole hours between {SettingKeys.SubscriptionIntervalMinHours} and {SettingKeys.SubscriptionIntervalMaxHours}";
                    return false;
                }

                value = hours.ToString(CultureInfo.InvariantCulture);
                return true;

            case SettingKeys.RouteTtl:
                if (!SettingKeys.TryParseRouteTtl(raw, out var ttl))
                {
                    error = $"{key} takes whole seconds";
                    return false;
                }

                value = ttl.ToString(CultureInfo.InvariantCulture);
                return true;

            default:
                return true;
        }
    }
}
