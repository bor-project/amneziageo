using System.Threading.Tasks;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Design-time-only data for the Avalonia previewer. Referenced from XAML via <c>Design.DataContext</c> so
/// the previewer renders a fully-populated screen — the real <see cref="MainWindowViewModel"/> backed by a
/// mocked, never-started <see cref="IAgentConnection"/> — instead of the empty first-run state (no config,
/// "нет связи с агентом") that shows when nothing has been loaded from the agent yet.
/// <para>
/// Every settings section is seeded, so switching <see cref="MainWindowViewModel.SettingsSection"/> below
/// (config / routing / sources / logs / general) previews a different, still-populated screen. The work
/// config is opened, so the Config detail editors render with content too.
/// </para>
/// <para>
/// Not constructed at runtime: Avalonia strips <c>Design.*</c> assignments outside design mode, so the
/// factory below never runs there. No IPC is issued — the sub-view-models are populated directly and their
/// agent delegates are no-ops — with one exception: opening a config auto-builds an
/// <see cref="ExportDialogViewModel"/> whose <c>LoadAsync</c> cannot reach the mock agent; it is replaced
/// below with a ready, pre-rendered instance, and its stray load fails harmlessly on the detached original.
/// </para>
/// </summary>
internal static class DesignData
{
    /// <summary>
    /// A fully-populated <see cref="MainWindowViewModel"/> parked on the Config settings page with the work
    /// config opened. Point <c>Design.DataContext</c> at this; change <c>SettingsSection</c> in the factory
    /// to preview a different screen (all sections carry sample data).
    /// </summary>
    public static MainWindowViewModel MainWindow { get; } = CreateMainWindow();

    /// <summary>
    /// Card catalogues for the previewer: configuration and routing cards in every state a card renders.
    /// The commands behind them are the real ones, so an interactive preview moves the frame between cards
    /// the way the app does.
    /// </summary>
    public static MainWindowViewModel Cards { get; } = CreateCards();

    /// <summary>
    /// The configuration card a preview of the control itself renders: the tunnel runs on it and the
    /// catalogue is on it, so it wears the frame in the connected colour.
    /// </summary>
    public static ConfigItemViewModel ConfigCard => Cards.Config.Configs[0];

    /// <summary>
    /// The routing card a preview of the control itself renders: traffic goes by this list and the catalogue
    /// is on it, so it wears the frame in the connected colour.
    /// </summary>
    public static RoutingListSummaryViewModel RoutingCard => Cards.Routing.RoutingLists[0];

    // No-op agent delegates: the sub-view-models never talk to a live agent at design time.
    private static Task NoSourceOp(SourceItemViewModel _) => Task.CompletedTask;

    private static MainWindowViewModel CreateMainWindow()
    {
        var connection = new NullAgentConnection();
        var vm = new MainWindowViewModel(connection, new UiPreferences())
        {
            SettingsSection = "config",
        };

        // A live, connected session so the header + power control render "connected" instead of the
        // "нет связи с агентом" first-run state.
        vm.Home.IsConnected = true;
        vm.Home.IsTunnelActive = true;
        vm.Home.BoundStatus = ConnectionStatus.Connected;
        vm.Home.BoundTarget = "de-frankfurt";

        // --- General: version/about (theme + language seed from prefs in the VM). ---
        vm.General.AppVersion = "AmneziaGeo 1.0.1.240";
        vm.General.EngineVersion = "AmneziaWG 1.5.0 · wstunnel 10.1.6";

        // --- Config catalogue ---
        var wsConfig = new ConfigItemViewModel
        {
            Name = "de-frankfurt",
            Endpoint = "vpn.example.com:9080",
            UseWebSocket = true,
            WebSocketHost = "vpn.example.com",
            WebSocketPort = 443,
            Mtu = 1280,
            UseIpv6 = true,
            Dns = "1.1.1.1, 2606:4700:4700::1111",
            Status = ConnectionStatus.Connected,
        };
        vm.Config.Configs.Add(wsConfig);
        vm.Config.Configs.Add(new ConfigItemViewModel
        {
            Name = "nl-amsterdam",
            Endpoint = "vpn2.example.com:51820",
            Mtu = 1420,
            Status = ConnectionStatus.Idle,
        });
        vm.HasConfigs = true;

        // --- Routing-list catalogue ---
        var rknList = new RoutingListSummaryViewModel
        {
            Id = 1, Name = "Обход РКН", RuleCount = 42, RouteCount = 131, DomainCount = 517,
            ProxyRuleCount = 30, DirectRuleCount = 9, BlockRuleCount = 3, UseGlobalProxy = true,
        };
        var mediaList = new RoutingListSummaryViewModel
        {
            Id = 2, Name = "YouTube + Discord", RuleCount = 6, RouteCount = 74, DomainCount = 39,
            ProxyRuleCount = 6, DirectRuleCount = 0, BlockRuleCount = 0, AllUdp = true,
        };
        vm.Routing.RoutingLists.Add(rknList);
        vm.Routing.RoutingLists.Add(mediaList);
        vm.Routing.HasRoutingLists = true;

        // --- Geo sources ---
        vm.Sources.Sources.Add(new SourceItemViewModel(NoSourceOp, NoSourceOp, NoSourceOp)
        {
            Kind = "geosite",
            CategoryCount = 1513,
            Updated = "2026-07-05 11:47",
            Url = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
        });
        vm.Sources.Sources.Add(new SourceItemViewModel(NoSourceOp, NoSourceOp, NoSourceOp)
        {
            Kind = "geoip",
            CategoryCount = 260,
            Updated = "2026-07-05 11:47",
            Url = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
        });
        vm.Sources.HasSources = true;

        // --- Diagnostics ---
        // The viewer text is seeded directly (design time has no agent to read the DB from).
        vm.Diagnostics.Logs.LogText = SampleLog;
        vm.Diagnostics.Logs.HasLogs = true;

        // --- Open the work config: renders the Config manage/transport editors. Building it makes a live
        // ConfigTransport (from wsConfig) and a stray ExportDialog whose LoadAsync cannot reach the mock
        // agent; the ready replacement below supersedes it.
        vm.Config.OpenConfig = "de-frankfurt";
        vm.Config.ConfigExport = ReadyExport(connection, "de-frankfurt", SampleConf);

        // --- Routing section editor: hand-built so it carries sample rules without an agent round-trip.
        // Pre-assigning RoutingEditor makes the EditRoutingList selection below short-circuit
        // BuildSectionRoutingEditor (matching id, not new), so the catalogue combo selects «Обход РКН»
        // without rebuilding the editor or issuing IPC.
        var routingEditor = new RoutingListEditorViewModel(connection, rknList.Id, rknList.Name);
        string[] rknRules =
        [
            "geosite:youtube",
            "geosite:googlevideo",
            "domain:chatgpt.com",
            "geosite:discord",
            "cidr:74.125.0.0/16",
            "app:svc=Discord",
        ];
        foreach (var rule in rknRules)
        {
            routingEditor.Rules.Add(rule);
        }
        vm.Routing.RoutingEditor = routingEditor;
        vm.Routing.RoutingSettings = new RoutingSettingsViewModel(connection, rknList.Id);
        vm.Routing.EditRoutingList = rknList;

        return vm;
    }

    // Both catalogues seeded for the gallery: a running tunnel, a ready configuration, one coming up, a
    // server that stopped answering, and a name longer than the card. No real screen carries all of them at
    // once - the gallery does, so a change to the card is judged against every state side by side.
    private static MainWindowViewModel CreateCards()
    {
        var vm = new MainWindowViewModel(new NullAgentConnection(), new UiPreferences());
        vm.Home.IsConnected = true;
        vm.Home.IsTunnelActive = true;
        vm.Home.BoundStatus = ConnectionStatus.Connected;
        vm.Home.BoundTarget = "de-frankfurt";

        vm.Config.Configs.Add(new ConfigItemViewModel
        {
            Name = "de-frankfurt",
            Endpoint = "vpn.example.com:9080",
            UseWebSocket = true,
            UseIpv6 = true,
            Mtu = 1280,
            Status = ConnectionStatus.Connected,
            IsSelected = true,
            IsPicked = true,
            HandshakeAgeSeconds = 11,
            LinkRttMs = 38,
            LinkLossPercent = 0,
            RxBitsPerSecond = 18_400_000,
            TxBitsPerSecond = 2_300_000,
        });
        vm.Config.Configs.Add(new ConfigItemViewModel
        {
            Name = "nl-amsterdam",
            Endpoint = "vpn2.example.com:51820",
            Mtu = 1420,
            IsSelected = false,
            ProbeState = ProbeOutcome.Alive,
            ProbeMilliseconds = 64,
        });
        vm.Config.Configs.Add(new ConfigItemViewModel
        {
            Name = "fi-helsinki",
            Endpoint = "vpn3.example.com:443",
            UseIpv6 = true,
            Status = ConnectionStatus.Connecting,
        });
        vm.Config.Configs.Add(new ConfigItemViewModel
        {
            Name = "us-newyork",
            Endpoint = "vpn4.example.com:51820",
            ProbeState = ProbeOutcome.NoAnswer,
            ProbeLossPercent = 100,
        });
        vm.Config.Configs.Add(new ConfigItemViewModel
        {
            Name = "Домашний сервер с именем во всю карточку",
            Endpoint = "very-long-host-name.example.com:51820",
            ProbeState = ProbeOutcome.Alive,
            ProbeMilliseconds = 268,
            ProbeLossPercent = 12,
        });
        vm.HasConfigs = true;

        vm.Routing.RoutingLists.Add(new RoutingListSummaryViewModel
        {
            Id = 1, Name = "Обход РКН", RuleCount = 42, RouteCount = 131, DomainCount = 517,
            ProxyRuleCount = 30, DirectRuleCount = 9, BlockRuleCount = 3,
            UseGlobalProxy = true, AllUdp = true, IsSelected = true, IsLive = true, IsPicked = true,
        });
        vm.Routing.RoutingLists.Add(new RoutingListSummaryViewModel
        {
            Id = 2, Name = "YouTube + Discord", RuleCount = 6, RouteCount = 74, DomainCount = 39,
            ProxyRuleCount = 6,
        });
        vm.Routing.RoutingLists.Add(new RoutingListSummaryViewModel
        {
            Id = 3, Name = "Список с именем, которое в карточку не влезает",
            RuleCount = 118, RouteCount = 940, DomainCount = 2140,
            ProxyRuleCount = 96, DirectRuleCount = 14, BlockRuleCount = 8, AllUdp = true,
        });
        vm.Routing.HasRoutingLists = true;

        return vm;
    }

    // A ready-to-display config area: the .conf text pre-loaded and its QR rendered, so no agent load is needed.
    private static ExportDialogViewModel ReadyExport(IAgentConnection connection, string name, string conf)
    {
        var export = new ExportDialogViewModel(connection, name);
        export.Seed(conf);
        return export;
    }

    // Representative wg-quick text (AmneziaWG obfuscation + WebSocket-carried peer). Keys are placeholders —
    // no real credentials live in source.
    private const string SampleConf =
        """
        [Interface]
        PrivateKey = SAMPLEdesignPRIVATEkeyDoNotUse0000000000000=
        Address = 10.8.3.2/32, fdbb:ad94:bacf:61a5::cafe:2/128
        DNS = 1.1.1.1, 2606:4700:4700::1111
        MTU = 1420
        Jc = 4
        Jmin = 40
        Jmax = 70
        S1 = 86
        S2 = 120
        H1 = 2128601981
        H2 = 246741798
        H3 = 599619293
        H4 = 1652909985
        S3 = 17
        S4 = 22
        I1 = <r 128>
        HeaderProtectionKey = QW1uZXppYUdlbyBkZXNpZ24tdGltZSBzYW1wbGVrZXk=
        ContentPaddingAddition = 12-44
        RekeyAfterTime = 100-135
        RekeyTimeout = 5-6
        RejectAfterTime = 186-259
        KeepaliveTimeout = 12-16
        MaxHandshakeAttempts = 17-33
        RandomTrailers = on
        DisableCookies = on

        [Peer]
        PublicKey = SAMPLEdesignSERVERpublicKey000000000000000=
        PresharedKey = SAMPLEdesignPRESHAREDkey00000000000000000000=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = vpn.example.com:9080
        PersistentKeepalive = 25
        """;

    // Representative agent journal (newest first, matching the viewer's rendering order): a slow initial
    // handshake that succeeds on retry, split-routing applied, reachability heals, then teardown.
    private const string SampleLog =
        """
        2026-07-08 20:16:52.028 [INF] wstunnel transport stopped
        2026-07-08 20:16:52.013 [INF] kill-switch disabled
        2026-07-08 20:16:52.012 [INF] connect de-frankfurt: session ended after 5822329 ms, tearing down
        2026-07-08 19:38:24.988 [INF] reachability heal www.youtube.com: last-good unreachable -> re-resolved to 216.58.201.174, 172.217.20.174 (+6)
        2026-07-08 19:38:06.178 [INF] reachability heal mobile.events.data.microsoft.com: last-good unreachable -> re-resolved to 52.168.117.175
        2026-07-08 18:39:55.302 [INF] set-routing-settings 1: dns='', excl=55 chars, allUdp=true, mode=split
        2026-07-08 18:39:54.487 [INF] de-frankfurt: handshake received in 4s
        2026-07-08 18:39:44.767 [WRN] de-frankfurt: server did not answer - no handshake, 0 B in 12s; unreachable
        2026-07-08 18:39:27.015 [INF] de-frankfurt: tunnel service responding over UAPI; waiting for handshake
        2026-07-08 18:39:16.283 [INF] config de-frankfurt: transport set - websocket=true, port=443, mtu=1280, host=wss://vpn.example.com:443/ag-…
        """;
}
