using System.Threading.Tasks;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Design-time-only data for the Avalonia previewer. Referenced from XAML via <c>Design.DataContext</c> so
/// the previewer renders a fully-populated screen — the real <see cref="MainWindowViewModel"/> backed by a
/// mocked, never-started <see cref="AgentConnection"/> — instead of the empty first-run state (no profile,
/// no config, "нет связи с агентом") that shows when nothing has been loaded from the agent yet.
/// <para>
/// Every settings section is seeded, so switching <see cref="MainWindowViewModel.SettingsSection"/> below
/// (profile / config / routing / sources / logs / general) — or <see cref="MainWindowViewModel.Nav"/> to
/// "home" — previews a different, still-populated screen. The work profile is opened, so the Profile and
/// Config detail editors render with content too.
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
    /// A fully-populated <see cref="MainWindowViewModel"/> parked on the Profile settings page with the work
    /// profile opened. Point <c>Design.DataContext</c> at this; change <c>SettingsSection</c> / <c>Nav</c> in
    /// the factory to preview a different screen (all sections carry sample data).
    /// </summary>
    public static MainWindowViewModel MainWindow { get; } = CreateMainWindow();

    // No-op agent delegates: the sub-view-models never talk to a live agent at design time.
    private static Task<IpcAck> NoProfileSave(string _, string __) => Task.FromResult(new IpcAck(true, string.Empty));
    private static Task<IpcAck> NoAssignRouting(string _, long? __, bool ___) => Task.FromResult(new IpcAck(true, string.Empty));
    private static Task NoSelectProfile(string _) => Task.CompletedTask;
    private static Task NoSetConnection(string _, bool __) => Task.CompletedTask;
    private static Task<IpcAck> NoRemoveConfig(string _) => Task.FromResult(new IpcAck(true, string.Empty));
    private static Task NoSourceOp(SourceItemViewModel _) => Task.CompletedTask;

    private static MainWindowViewModel CreateMainWindow()
    {
        var connection = new AgentConnection();
        var vm = new MainWindowViewModel(connection, new UiPreferences())
        {
            Nav = "settings",
            SettingsSection = "profile",
            // A live, connected session so the header + power control render "connected" instead of the
            // "нет связи с агентом" first-run state.
            IsConnected = true,
            IsTunnelActive = true,
            BoundStatus = ConnectionStatus.Connected,
            BoundTarget = "de-frankfurt",
            AppVersion = "AmneziaGeo 1.0.1.240",
            AmneziaVersion = "AmneziaWG 1.5.0 · wstunnel 10.1.6",
        };

        // --- Config catalogue ---
        var borConfig = new ConfigItemViewModel
        {
            Name = "de-frankfurt",
            Endpoint = "vpn.example.com:9080",
            UseWebSocket = true,
            WebSocketHost = "vpn.example.com",
            WebSocketPort = 443,
            Mtu = 1280,
            Dns = "1.1.1.1, 2606:4700:4700::1111",
            Status = ConnectionStatus.Connected,
        };
        vm.Configs.Add(borConfig);
        vm.Configs.Add(new ConfigItemViewModel
        {
            Name = "nl-amsterdam",
            Endpoint = "vpn2.example.com:51820",
            Mtu = 1420,
            Status = ConnectionStatus.Idle,
        });
        vm.ConfigCatalogueOptions.Add(new ConfigChoice("de-frankfurt"));
        vm.ConfigCatalogueOptions.Add(new ConfigChoice("nl-amsterdam"));
        vm.HasConfigs = true;

        // Shared option lists for the per-profile config / routing combos.
        ConfigChoice[] configChoices = [ConfigChoice.None, new ConfigChoice("de-frankfurt"), new ConfigChoice("nl-amsterdam")];

        // --- Routing-list catalogue ---
        var rknList = new RoutingListSummaryViewModel { Id = 1, Name = "Обход РКН", RuleCount = 42, RouteCount = 131, DomainCount = 517 };
        var mediaList = new RoutingListSummaryViewModel { Id = 2, Name = "YouTube + Discord", RuleCount = 6, RouteCount = 74, DomainCount = 39 };
        vm.RoutingLists.Add(rknList);
        vm.RoutingLists.Add(mediaList);
        vm.RoutingCatalogueOptions.Add(new RoutingListChoice(rknList.Id, rknList.Name));
        vm.RoutingCatalogueOptions.Add(new RoutingListChoice(mediaList.Id, mediaList.Name));
        vm.HasRoutingLists = true;

        RoutingListChoice[] routingChoices =
        [
            RoutingListChoice.None,
            new RoutingListChoice(rknList.Id, rknList.Name),
            new RoutingListChoice(mediaList.Id, mediaList.Name),
        ];

        // --- Profiles (config × routing) ---
        var workProfile = NewProfile("Новый профиль", "de-frankfurt", ConnectionStatus.Connected,
            configChoices, routingChoices, RoutingListChoice.None);
        var homeProfile = NewProfile("Дом (RU)", "nl-amsterdam", ConnectionStatus.Disconnected,
            configChoices, routingChoices, new RoutingListChoice(mediaList.Id, mediaList.Name));
        vm.Balancers.Add(workProfile);
        vm.Balancers.Add(homeProfile);
        vm.ProfileOptions.Add(new ProfileChoice(workProfile.Name));
        vm.ProfileOptions.Add(new ProfileChoice(homeProfile.Name));
        vm.HasBalancers = true;

        // --- Geo sources ---
        vm.Sources.Add(new SourceItemViewModel(NoSourceOp, NoSourceOp)
        {
            Kind = "geosite",
            CategoryCount = 1513,
            Updated = "2026-07-05 11:47",
            Url = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
        });
        vm.Sources.Add(new SourceItemViewModel(NoSourceOp, NoSourceOp)
        {
            Kind = "geoip",
            CategoryCount = 260,
            Updated = "2026-07-05 11:47",
            Url = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
        });
        vm.HasSources = true;

        // --- Logs ---
        vm.LogFiles.Add(new LogFileChoice("ageo-20260708_001.log", "agent", 147865, "2026-07-08 20:30"));
        vm.LogFiles.Add(new LogFileChoice("routes.log", "routes", 26317, "2026-07-08 20:16"));
        // SelectedLogFile is left null on purpose: selecting a file kicks off an agent read. The viewer text is
        // seeded directly instead so it shows without a round-trip.
        vm.LogText = SampleLog;
        vm.HasLogs = true;

        // --- Open the work profile: renders the Profile editor + the Config manage/transport editors. This
        // sets OpenConfig = "de-frankfurt", which builds a live ConfigTransport (from borConfig) and a stray
        // ExportDialog whose LoadAsync cannot reach the mock agent; the ready replacement below supersedes it.
        vm.OpenProfile = workProfile;
        vm.ConfigExport = ReadyExport(connection, "de-frankfurt", SampleConf);

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
        vm.RoutingEditor = routingEditor;
        vm.RoutingSettings = new RoutingSettingsViewModel(connection, rknList.Id);
        vm.EditRoutingList = rknList;

        return vm;
    }

    private static BalancerItemViewModel NewProfile(
        string name,
        string config,
        string status,
        ConfigChoice[] configChoices,
        RoutingListChoice[] routingChoices,
        RoutingListChoice routing)
    {
        var profile = new BalancerItemViewModel(NoProfileSave, NoAssignRouting, NoSelectProfile, NoSetConnection, NoRemoveConfig)
        {
            Name = name,
            Config = config,
            Status = status,
        };
        foreach (var choice in configChoices)
        {
            profile.ConfigOptions.Add(choice);
        }

        foreach (var choice in routingChoices)
        {
            profile.RoutingListOptions.Add(choice);
        }

        profile.SelectedConfig = configChoices.FirstOrDefault(c => c.IsReal && c.Name == config) ?? ConfigChoice.None;
        profile.SelectedRoutingList = routing;
        return profile;
    }

    // A ready-to-display export: the .conf text pre-loaded and its QR rendered, so no agent load is needed.
    private static ExportDialogViewModel ReadyExport(AgentConnection connection, string name, string conf)
    {
        var export = new ExportDialogViewModel(connection, name)
        {
            Payload = conf,
            IsReady = true,
        };
        try
        {
            export.QrImage = QrCodec.Generate(conf);
        }
        catch
        {
            // No QR at design time is fine; the view falls back to its "unavailable" hint.
        }

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

        [Peer]
        PublicKey = SAMPLEdesignSERVERpublicKey000000000000000=
        PresharedKey = SAMPLEdesignPRESHAREDkey00000000000000000000=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = vpn.example.com:9080
        PersistentKeepalive = 25
        """;

    // A slice of a real agent journal (newest first, matching the viewer's rendering order): a slow initial
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
