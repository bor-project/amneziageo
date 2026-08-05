using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

using AmneziaGeo.Cli;

namespace AmneziaGeo.Linux.Cli.Tui;

/// <summary>
/// The console UI shell: a section rail on the left and the section's view on the right.
/// </summary>
internal sealed class Shell : Window
{
    private readonly IAgentLink _agent;
    private readonly Label _state;
    private readonly ListView _rail;
    private readonly FrameView _content;
    private readonly string[] _sections;

    /// <summary>
    /// ctor
    /// </summary>
    public Shell(IAgentLink agent)
    {
        _agent = agent;
        Title = Localized("Tui_Title");
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _sections =
        [
            Localized("Tui_SectionStatus"),
            Localized("Main_RailProfileTitle"),
            Localized("Main_RailConfigTitle"),
            Localized("Main_RailRoutingTitle"),
            Localized("Main_RailSourcesTitle"),
            Localized("Main_RailGeneralTitle"),
            Localized("Main_RailLogsTitle"),
        ];

        _state = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Text = string.Empty };
        _rail = new ListView { X = 0, Y = 2, Width = 24, Height = Dim.Fill(1) };
        _rail.SetSource(new ObservableCollection<string>(_sections));
        _content = new FrameView { X = 25, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(1) };
        var hint = new Label { X = 1, Y = Pos.AnchorEnd(1), Text = Localized("Tui_Hint") };

        _rail.ValueChanged += (_, _) => Show();
        Add(_state, _rail, _content, hint);

        _agent.SnapshotReceived += OnSnapshot;
        Refresh();
        _rail.SetFocus();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _agent.SnapshotReceived -= OnSnapshot;
        }

        base.Dispose(disposing);
    }

    private static string Localized(string key) => Loc.Instance.Get(key);

    private void OnSnapshot(StatusSnapshot snapshot) => Application.Invoke(Header);

    private void Refresh()
    {
        Header();
        Show();
    }

    private void Header()
    {
        var snapshot = _agent.Snapshot;
        _state.Text = $"{snapshot.BoundStatus} · {snapshot.SelectedTarget ?? Localized("Main_NotSelected")} · " +
            $"{Localized("Main_RailRoutingTitle")}: {(snapshot.RoutingLists?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} · " +
            $"{Localized("Main_LogVerbosityTitle")}: {snapshot.LogLevel}";
    }

    private void Show()
    {
        _content.RemoveAll();
        var index = _rail.SelectedItem ?? 0;
        _content.Title = _sections[Math.Clamp(index, 0, _sections.Length - 1)];
        var view = index switch
        {
            1 => Profiles(),
            2 => Configs(),
            3 => Routing(),
            4 => Sources(),
            5 => Settings(),
            6 => Log(),
            _ => Status(),
        };

        _content.Add(view);
    }

    // Blocks the UI thread for the round trip; commands answer in milliseconds, downloads warn first.
    private IpcAck Send(string op, params string[] args) =>
        _agent.SendAsync(op, args).GetAwaiter().GetResult();

    private bool Apply(IpcAck ack)
    {
        if (!ack.Ok)
        {
            Prompt.Error(AckText.Localize(ack.Message));
            return false;
        }

        Refresh();
        return true;
    }

    private View Panel(View body, params Button[] buttons)
    {
        var host = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        body.X = 0;
        body.Y = 0;
        body.Width = Dim.Fill();
        body.Height = Dim.Fill(2);
        host.Add(body);

        var offset = 0;
        foreach (var button in buttons)
        {
            button.X = offset;
            button.Y = Pos.AnchorEnd(1);
            host.Add(button);
            offset += button.Text.Length + 6;
        }

        return host;
    }

    private static Button Action(string label, Action handler)
    {
        var button = new Button { Text = label };
        button.Accepting += (_, _) => handler();
        return button;
    }

    private static ListView Rows(IReadOnlyList<string> rows)
    {
        var list = new ListView();
        list.SetSource(new ObservableCollection<string>([.. rows]));
        return list;
    }

    private View Status()
    {
        var snapshot = _agent.Snapshot;
        var lines = new List<string>
        {
            $"{Localized("Tui_SectionStatus")}: {snapshot.BoundStatus}",
            $"{Localized("Main_RailProfileTitle")}: {snapshot.SelectedTarget ?? Localized("Main_NotSelected")}",
            $"{Localized("Tui_Tunnel")}: {(snapshot.Active ? Localized("Tui_Up") : Localized("Tui_Down"))}",
            $"{Localized("General_SurviveReboot")}: {OnOff(snapshot.SurviveReboot)}",
            $"{Localized("General_PeriodicReconnect")}: {OnOff(snapshot.PeriodicReconnect)} ({snapshot.PeriodicReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture)})",
            $"{Localized("Main_LogVerbosityTitle")}: {snapshot.LogLevel}",
            $"{Localized("Tui_Agent")}: {snapshot.AgentVersion}",
        };

        if (snapshot.ConnectFailed)
        {
            lines.Add($"{Localized("Tui_LastFailure")}: {snapshot.ConnectFailReason} {snapshot.ConnectFailDetail}".TrimEnd());
        }

        return Panel(
            new TextView { Text = string.Join('\n', lines), ReadOnly = true },
            Action(Localized("Tui_Connect"), () => Apply(Send(IpcContract.OpSetConnection, "connect"))),
            Action(Localized("Tui_Disconnect"), () => Apply(Send(IpcContract.OpSetConnection, "disconnect"))),
            Action(Localized("Tui_Refresh"), Refresh));
    }

    private View Profiles()
    {
        var snapshot = _agent.Snapshot;
        var profiles = snapshot.Profiles;
        var list = Rows([.. profiles.Select(profile =>
            $"{(profile.Name == snapshot.SelectedTarget ? "*" : " ")} {profile.Name}  [{(profile.Config.Length > 0 ? profile.Config : "-")}]  {profile.Status}")]);

        ProfileEntry? Current() => Pick(list, profiles);

        return Panel(
            list,
            Action(Localized("Tui_Select"), () =>
            {
                if (Current() is { } profile)
                {
                    Apply(Send(IpcContract.OpSelectProfile, profile.Name));
                }
            }),
            Action(Localized("Tui_Connect"), () =>
            {
                if (Current() is { } profile && Send(IpcContract.OpSelectProfile, profile.Name).Ok)
                {
                    Apply(Send(IpcContract.OpSetConnection, "connect"));
                }
            }),
            Action(Localized("Tui_Assign"), () =>
            {
                if (Current() is { } profile)
                {
                    Assign(profile);
                }
            }),
            Action(Localized("Main_ConfirmDeleteButton"), () =>
            {
                if (Current() is { } profile && Prompt.Confirm(Localized("Main_RailProfileTitle"), Confirm(profile.Name)))
                {
                    Apply(Send(IpcContract.OpRemoveProfile, profile.Name));
                }
            }));
    }

    private void Assign(ProfileEntry profile)
    {
        var lists = _agent.Snapshot.RoutingLists ?? [];
        var labels = new List<string> { Localized("Main_NotSelected") };
        labels.AddRange(lists.Select(list => $"{list.Name} ({list.RuleCount.ToString(CultureInfo.InvariantCulture)})"));
        if (Prompt.Pick(Localized("Main_RailRoutingTitle"), labels) is not { } chosen)
        {
            return;
        }

        var (id, use) = chosen == 0
            ? ("none", "off")
            : (lists[chosen - 1].Id.ToString(CultureInfo.InvariantCulture), "on");
        Apply(Send(IpcContract.OpAssignRouting, profile.Name, id, use));
    }

    private View Configs()
    {
        var configs = _agent.Snapshot.Configs;
        var list = Rows([.. configs.Select(config => $"{config.Name}  {(config.Endpoint.Length > 0 ? config.Endpoint : "-")}  {config.Status}")]);

        ConfigEntry? Current() => Pick(list, configs);

        return Panel(
            list,
            Action(Localized("Tui_Show"), () =>
            {
                if (Current() is { } config)
                {
                    var ack = Send(IpcContract.OpGetConfig, config.Name);
                    if (ack.Ok)
                    {
                        Prompt.View(config.Name, ack.Message);
                    }
                    else
                    {
                        Prompt.Error(AckText.Localize(ack.Message));
                    }
                }
            }),
            Action(Localized("Main_ImportButton"), Import),
            Action(Localized("Tui_ShareLink"), () =>
            {
                if (Current() is { } config)
                {
                    var ack = Send(IpcContract.OpGetConfig, config.Name);
                    if (ack.Ok)
                    {
                        Prompt.View(config.Name, VpnLinkCodec.Encode(ack.Message, config.Name));
                    }
                    else
                    {
                        Prompt.Error(AckText.Localize(ack.Message));
                    }
                }
            }),
            Action(Localized("Main_ConfirmDeleteButton"), () =>
            {
                if (Current() is { } config && Prompt.Confirm(Localized("Main_RailConfigTitle"), Confirm(config.Name)))
                {
                    Apply(Send(IpcContract.OpRemoveConfig, config.Name));
                }
            }));
    }

    private void Import()
    {
        if (Prompt.Block(Localized("Main_ImportButton"), Localized("Tui_ImportHint")) is not { } payload)
        {
            return;
        }

        var imported = VpnLinkCodec.TryDecode(payload);
        var confText = imported?.ConfText ?? payload;
        var taken = _agent.Snapshot.Configs.Select(config => config.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestion = imported?.Name ?? VpnLinkCodec.HostName(confText) ?? string.Empty;
        if (Prompt.Line(Localized("Main_ImportButton"), Localized("Main_NameLabel"), UniqueName.ResolveParen(suggestion, taken)) is not { } name)
        {
            return;
        }

        Apply(Send(IpcContract.OpImportConfig, name, confText));
    }

    private View Routing()
    {
        var lists = _agent.Snapshot.RoutingLists ?? [];
        var list = Rows([.. lists.Select(entry =>
            $"#{entry.Id.ToString(CultureInfo.InvariantCulture)} {entry.Name}  {entry.RuleCount.ToString(CultureInfo.InvariantCulture)} / {entry.RouteCount.ToString(CultureInfo.InvariantCulture)} / {entry.DomainCount.ToString(CultureInfo.InvariantCulture)}")]);

        RoutingListEntry? Current() => Pick(list, lists);

        return Panel(
            list,
            Action(Localized("Tui_Rules"), () =>
            {
                if (Current() is { } entry)
                {
                    EditRules(entry);
                }
            }),
            Action(Localized("Tui_Settings"), () =>
            {
                if (Current() is { } entry)
                {
                    RoutingSettings(entry);
                }
            }),
            Action(Localized("Main_ConfirmDeleteButton"), () =>
            {
                if (Current() is { } entry && Prompt.Confirm(Localized("Main_RailRoutingTitle"), Confirm(entry.Name)))
                {
                    Apply(Send(IpcContract.OpRemoveRoutingList, entry.Id.ToString(CultureInfo.InvariantCulture)));
                }
            }));
    }

    private void EditRules(RoutingListEntry entry)
    {
        var id = entry.Id.ToString(CultureInfo.InvariantCulture);
        var current = Send(IpcContract.OpGetRoutingList, id);
        if (!current.Ok)
        {
            Prompt.Error(AckText.Localize(current.Message));
            return;
        }

        if (Prompt.Block(entry.Name, Localized("Tui_RulesHint"), current.Message) is not { } edited)
        {
            return;
        }

        var rules = edited.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (Rules.FirstInvalid(rules) is { } invalid)
        {
            Prompt.Error(Loc.Instance.Get("Tui_BadRule", invalid));
            return;
        }

        Apply(Send(IpcContract.OpSaveRoutingList, [id, entry.Name, .. rules]));
    }

    private void RoutingSettings(RoutingListEntry entry)
    {
        var id = entry.Id.ToString(CultureInfo.InvariantCulture);
        var stored = Send(IpcContract.OpGetRoutingSettings, id);
        if (!stored.Ok)
        {
            Prompt.Error(AckText.Localize(stored.Message));
            return;
        }

        var settings = System.Text.Json.JsonSerializer.Deserialize<Traffic>(stored.Message, IpcJson.Options)
            ?? new Traffic(string.Empty, false, "split", false);

        var dialog = new Dialog { Title = entry.Name, Width = Dim.Percent(75), Height = Dim.Percent(70) };
        var udp = new CheckBox { Text = Localized("Main_AllUdpTitle"), X = 1, Y = 0, Value = State(settings.AllUdp) };
        var proxy = new CheckBox { Text = Localized("Main_GlobalProxyTitle"), X = 1, Y = 1, Value = State(settings.UseGlobalProxy) };
        var caption = new Label { Text = Localized("Main_ExclusionsLabel"), X = 1, Y = 3 };
        var editor = new TextView { Text = settings.Exclusions, X = 1, Y = 4, Width = Dim.Fill(2), Height = Dim.Fill(3) };
        var saved = false;

        var save = new Button { Text = Localized("Tui_Save"), IsDefault = true, X = 1, Y = Pos.AnchorEnd(1) };
        save.Accepting += (_, _) =>
        {
            saved = true;
            Application.RequestStop();
        };

        var cancel = new Button { Text = Localized("Main_CancelButton"), X = 16, Y = Pos.AnchorEnd(1) };
        cancel.Accepting += (_, _) => Application.RequestStop();

        dialog.Add(udp, proxy, caption, editor, save, cancel);
        Application.Run(dialog);

        if (saved)
        {
            var globalProxy = proxy.Value == CheckState.Checked;
            Apply(Send(
                IpcContract.OpSetRoutingSettings,
                id,
                editor.Text,
                Toggle.Text(udp.Value == CheckState.Checked),
                globalProxy ? "full" : "split",
                Toggle.Text(globalProxy)));
        }

        dialog.Dispose();
    }

    private View Sources()
    {
        var sources = _agent.Snapshot.Sources ?? [];
        var list = Rows([.. sources.Select(source =>
            $"{source.Name}  {source.Kind}  {source.CategoryCount.ToString(CultureInfo.InvariantCulture)}  {source.Updated ?? "-"}")]);

        SourceEntry? Current() => Pick(list, sources);

        return Panel(
            list,
            Action(Localized("Tui_UpdateAll"), () =>
            {
                Prompt.Info(Localized("Tui_Downloading"));
                Apply(Send(IpcContract.OpUpdateSources));
            }),
            Action(Localized("Tui_Refresh"), () =>
            {
                if (Current() is { } source)
                {
                    Prompt.Info(Localized("Tui_Downloading"));
                    Apply(Send(IpcContract.OpUpdateSource, source.Name));
                }
            }),
            Action(Localized("Main_ConfirmDeleteButton"), () =>
            {
                if (Current() is { } source && Prompt.Confirm(Localized("Main_RailSourcesTitle"), Confirm(source.Name)))
                {
                    Apply(Send(IpcContract.OpRemoveSource, source.Name));
                }
            }));
    }

    private View Settings()
    {
        var snapshot = _agent.Snapshot;
        var levels = new[] { "error", "warning", "info", "debug", "trace" };
        var host = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

        var levelCaption = new Label { Text = Localized("Main_LogVerbosityTitle"), X = 1, Y = 0 };
        var level = new OptionSelector
        {
            X = 1,
            Y = 1,
            Labels = levels,
            Value = Math.Max(0, Array.IndexOf(levels, snapshot.LogLevel)),
        };

        var routeLog = new CheckBox { Text = Localized("Main_RouteLogTitle"), X = 1, Y = 7, Value = State(snapshot.RouteLog) };
        var survive = new CheckBox { Text = Localized("General_SurviveReboot"), X = 1, Y = 8, Value = State(snapshot.SurviveReboot) };
        var periodic = new CheckBox { Text = Localized("General_PeriodicReconnect"), X = 1, Y = 9, Value = State(snapshot.PeriodicReconnect) };
        var intervalCaption = new Label { Text = Localized("General_ReconnectInterval"), X = 1, Y = 11 };
        var interval = new TextField
        {
            X = 1,
            Y = 12,
            Width = 10,
            Text = snapshot.PeriodicReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture),
        };

        var save = Action(Localized("Tui_Save"), () =>
        {
            var chosen = levels[Math.Clamp(level.Value ?? 0, 0, levels.Length - 1)];
            var ack = Send(IpcContract.OpSetSetting, "log-level", chosen);
            if (ack.Ok)
            {
                ack = Send(IpcContract.OpSetSetting, "route-log", Toggle.Text(routeLog.Value == CheckState.Checked));
            }

            if (ack.Ok)
            {
                ack = Send(IpcContract.OpSetSetting, "survive-reboot", Toggle.Text(survive.Value == CheckState.Checked));
            }

            if (ack.Ok)
            {
                ack = Send(IpcContract.OpSetSetting, "periodic-reconnect-enabled", Toggle.Text(periodic.Value == CheckState.Checked));
            }

            if (ack.Ok && int.TryParse(interval.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                ack = Send(IpcContract.OpSetSetting, "periodic-reconnect-interval-seconds", Math.Clamp(seconds, 5, 3600).ToString(CultureInfo.InvariantCulture));
            }

            if (Apply(ack))
            {
                Prompt.Info(Localized("Tui_Saved"));
            }
        });

        save.X = 1;
        save.Y = 14;
        host.Add(levelCaption, level, routeLog, survive, periodic, intervalCaption, interval, save);
        return host;
    }

    private View Log()
    {
        var search = new TextField { X = 1, Y = 0, Width = 30, Text = string.Empty };
        var viewer = new TextView { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(2), ReadOnly = true };
        var host = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        var caption = new Label { Text = Localized("Main_LogSearchWatermark"), X = 33, Y = 0 };

        void Load()
        {
            var ack = Send(IpcContract.OpReadLog, "ageo", "300", "0", string.Empty, search.Text ?? string.Empty);
            if (!ack.Ok)
            {
                Prompt.Error(AckText.Localize(ack.Message));
                return;
            }

            var page = System.Text.Json.JsonSerializer.Deserialize<Page>(ack.Message, IpcJson.Options);
            var lines = page?.Lines ?? [];
            viewer.Text = lines.Count == 0 ? Localized("Tui_LogEmpty") : string.Join('\n', lines);
        }

        var refresh = Action(Localized("Tui_Refresh"), Load);
        refresh.X = 0;
        refresh.Y = Pos.AnchorEnd(1);

        var clear = Action(Localized("Main_ClearLogButton"), () =>
        {
            if (Prompt.Confirm(Localized("Main_RailLogsTitle"), Localized("Main_ClearLogButton")))
            {
                Send(IpcContract.OpClearLog, "ageo");
                Load();
            }
        });

        clear.X = 14;
        clear.Y = Pos.AnchorEnd(1);

        host.Add(search, caption, viewer, refresh, clear);
        Load();
        return host;
    }

    private static T? Pick<T>(ListView list, IReadOnlyList<T> items)
        where T : class
    {
        var index = list.SelectedItem ?? -1;
        return index >= 0 && index < items.Count ? items[index] : null;
    }

    private static CheckState State(bool on) => on ? CheckState.Checked : CheckState.UnChecked;

    private static string OnOff(bool on) => Loc.Instance.Get(on ? "Tui_On" : "Tui_Off");

    private static string Confirm(string name) => Loc.Instance.Get("Tui_ConfirmDelete", name);

    /// <summary>
    /// Traffic settings of a routing list.
    /// </summary>
    private sealed record Traffic(string Exclusions, bool AllUdp, string Mode, bool UseGlobalProxy);

    /// <summary>
    /// One page of the agent log.
    /// </summary>
    private sealed record Page(IReadOnlyList<string> Lines, long FirstId, bool HasOlder, int MatchCount);
}
