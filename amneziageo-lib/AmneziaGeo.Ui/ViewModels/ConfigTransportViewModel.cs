using System.Globalization;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// The transport settings of a config: the WebSocket switch, open while the server of the config offers a front, the MTU with its mode, IPv6, the router, inbound access and routing. Saving sends set-websocket.
/// </summary>
internal sealed partial class ConfigTransportViewModel : ViewModelBase, IEditScope
{
    private readonly IAgentConnection _connection;

    // Baseline captured on load / commit / import; the transport is dirty when a field differs from it (#143).
    private bool _baseUseWebSocket;
    private string _baseMtu = string.Empty;
    private int _baseMtuMode;
    private bool _baseUseIpv6;
    private bool _baseUseRouter;
    private bool _baseAllowInbound;
    private bool _baseUseRouting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebSocketOn))]
    private bool _useWebSocket;

    /// <summary>
    /// Whether the server of the configuration offers a websocket front.
    /// </summary>
    public bool WebSocketOpen { get; }

    /// <summary>
    /// Whether the tunnel is carried inside a websocket; off wherever the server offers no front.
    /// </summary>
    public bool WebSocketOn
    {
        get => UseWebSocket && WebSocketOpen;
        set
        {
            if (WebSocketOpen)
            {
                UseWebSocket = value;
            }
        }
    }

    [ObservableProperty]
    private string _mtu = string.Empty;

    // Where the size comes from: 0 the link, 1 the config text, 2 this field.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMtuReadOnly))]
    private int _mtuMode;

    [ObservableProperty]
    private bool _useIpv6;

    [ObservableProperty]
    private bool _useRouter = true;

    [ObservableProperty]
    private bool _allowInbound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutingOn))]
    private bool _useRouting = true;

    /// <summary>
    /// Whether the server of the configuration leaves routing to this device.
    /// </summary>
    public bool RoutingOpen { get; }

    /// <summary>
    /// Whether the configuration takes the routing list; off wherever its server bans routing.
    /// </summary>
    public bool RoutingOn
    {
        get => UseRouting && RoutingOpen;
        set
        {
            if (RoutingOpen)
            {
                UseRouting = value;
            }
        }
    }

    // The reach of the access the agent holds: the whole tunnel network or the server alone.
    private bool _inboundNetwork;

    /// <summary>
    /// The address this machine answers at inside the tunnel.
    /// </summary>
    public string TunnelAddress { get; }

    /// <summary>
    /// Whether that address stands in the interface.
    /// </summary>
    public bool ShowInboundAddress => InboundAvailable && TunnelAddress.Length > 0;

    /// <summary>
    /// Whether this platform holds connections from the tunnel off the machine; only there is the switch worth showing.
    /// </summary>
    public static bool InboundAvailable => !OperatingSystem.IsAndroid();

    // Keeps the router switch out of the interface.
    internal static bool RouterVisible => false;

    /// <summary>
    /// Whether this platform can decide connections of its own at all; only there is the switch worth showing.
    /// </summary>
    public static bool RouterAvailable => RouterVisible && OperatingSystem.IsAndroid();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // Narrow-pane layout flag, pushed by the view from its own width.
    [ObservableProperty]
    private bool _isCompact;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigTransportViewModel(IAgentConnection connection, string name, bool useWebSocket, int mtu, bool useIpv6, MtuMode mtuMode = AmneziaGeo.Decl.MtuMode.Auto, int resolvedMtu = 0, bool useRouter = true, bool allowInbound = false, bool inboundNetwork = false, string address = "", bool webSocketOffered = false, bool useRouting = true, bool routingLocked = false)
    {
        _connection = connection;
        ConfigName = name;
        _useWebSocket = useWebSocket;
        WebSocketOpen = webSocketOffered;
        _useIpv6 = useIpv6;
        _useRouter = useRouter;
        _allowInbound = allowInbound;
        _inboundNetwork = inboundNetwork;
        _useRouting = useRouting;
        RoutingOpen = !routingLocked;
        TunnelAddress = FormatAddresses(address);
        _mtuMode = (int)mtuMode;

        // Only the custom mode shows a size of its own; the other two show what the agent settled on.
        var shown = mtuMode == AmneziaGeo.Decl.MtuMode.Custom ? mtu : resolvedMtu > 0 ? resolvedMtu : mtu;
        _mtu = shown > 0 ? shown.ToString(CultureInfo.InvariantCulture) : "1420";

        // Seeded (backing fields set, no OnChanged fired): this state is the clean baseline (#143).
        CaptureBaseline();
    }

    // Dirty tracking suppressed while applying an import / reverting so those bulk field writes do not flip the
    // flag mid-way (#143). Field changes mark the item dirty; the header Save commits, the header Cancel reverts.
    private bool _applying;

    /// <summary>
    /// Autosave the open-config transport: a toggle / combo commits at once, text fields on blur.
    /// </summary>
    public bool AutoSave { get; set; }

    private bool _committing;
    private bool _commitPending;

    partial void OnUseWebSocketChanged(bool value)
    {
        MarkDirty();
        FireAutoSave();
    }

    partial void OnMtuChanged(string value) => MarkDirty();

    partial void OnMtuModeChanged(int value)
    {
        MarkDirty();
        FireAutoSave();
    }

    /// <summary>
    /// Whether the size is the client's to pick, which leaves the field to read.
    /// </summary>
    public bool IsMtuReadOnly => MtuModes.IsReadOnly(MtuModes.From(MtuMode));

    partial void OnUseIpv6Changed(bool value)
    {
        MarkDirty();
        FireAutoSave();
    }

    partial void OnUseRouterChanged(bool value)
    {
        MarkDirty();
        FireAutoSave();
    }

    partial void OnAllowInboundChanged(bool value)
    {
        MarkDirty();
        FireAutoSave();
    }

    partial void OnUseRoutingChanged(bool value)
    {
        MarkDirty();
        FireAutoSave();
    }

    /// <inheritdoc />
    public bool IsDirty { get; private set; }

    /// <inheritdoc />
    public event EventHandler? DirtyChanged;

    private void MarkDirty()
    {
        if (_applying)
        {
            return;
        }

        // Any edit clears a stale validation / status line (#3).
        StatusMessage = string.Empty;

        var dirty = UseWebSocket != _baseUseWebSocket
            || !string.Equals(Mtu, _baseMtu, StringComparison.Ordinal)
            || MtuMode != _baseMtuMode
            || UseIpv6 != _baseUseIpv6
            || UseRouter != _baseUseRouter
            || AllowInbound != _baseAllowInbound
            || UseRouting != _baseUseRouting;
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void CaptureBaseline()
    {
        _baseUseWebSocket = UseWebSocket;
        _baseMtu = Mtu ?? string.Empty;
        _baseMtuMode = MtuMode;
        _baseUseIpv6 = UseIpv6;
        _baseUseRouter = UseRouter;
        _baseAllowInbound = AllowInbound;
        _baseUseRouting = UseRouting;
        if (IsDirty)
        {
            IsDirty = false;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Revert()
    {
        _applying = true;
        try
        {
            UseWebSocket = _baseUseWebSocket;
            Mtu = _baseMtu;
            MtuMode = _baseMtuMode;
            UseIpv6 = _baseUseIpv6;
            UseRouter = _baseUseRouter;
            AllowInbound = _baseAllowInbound;
            UseRouting = _baseUseRouting;
            StatusMessage = string.Empty;
        }
        finally
        {
            _applying = false;
        }

        MarkDirty();
    }

    /// <summary>
    /// The configuration name being edited.
    /// </summary>
    public string ConfigName { get; private set; }

    /// <summary>
    /// Retargets these settings at a (newly-created) config name before committing them. Used by the config
    /// create form, which builds the transport editor before the config exists and only knows the final name
    /// at save time (#143).
    /// </summary>
    public void Retarget(string name) => ConfigName = name;

    /// <summary>
    /// Whether this platform carries the tunnel over a WebSocket proxy.
    /// </summary>
    public bool SupportsWebSocket => UiPlatform.SupportsWebSocket;

    /// <summary>
    /// Whether the platform note replaces the WebSocket controls.
    /// </summary>
    public bool WebSocketUnavailable => !UiPlatform.SupportsWebSocket;

    /// <inheritdoc />
    public bool CanCommit()
    {
        // MTU: empty = default; validate 576-1500. The other modes pick the size themselves, so the field is theirs.
        var mtuVal = Mtu.Trim();
        if (!IsMtuReadOnly && mtuVal.Length > 0
            && (!int.TryParse(mtuVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mtu) || mtu is < MtuModes.MinMtu or > MtuModes.MaxMtu))
        {
            StatusMessage = Loc.Instance.Get("Transport_InvalidMtu");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Persists the transport settings through the agent (#143 header Save); returns whether it succeeded. An
    /// invalid MTU fails without a write and surfaces its reason, keeping the item dirty. Applies on reconnect.
    /// </summary>
    public async Task<bool> CommitAsync()
    {
        if (!CanCommit())
        {
            return false;
        }

        IsBusy = true;
        try
        {
            // A size travels only with the mode that takes one; the others keep whatever was stored.
            var mtuVal = IsMtuReadOnly ? string.Empty : Mtu.Trim();
            var network = WholeNetwork();
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetWebSocket,
                [ConfigName, UseWebSocket ? "on" : "off", mtuVal, UseIpv6 ? "on" : "off", MtuModes.Text(MtuModes.From(MtuMode)), UseRouter ? "on" : "off", AllowInbound ? "on" : "off", network ? "on" : "off", UseRouting ? "on" : "off"]));
            if (ack.Ok)
            {
                _inboundNetwork = network;
            }

            // Only a failure reason stays inline; a reconnect need shows as the mark by the connect control (RestartRequired).
            StatusMessage = ack.Ok ? string.Empty : ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // The reach of the access to send: the one the agent holds while the switch stays on, the whole network once it is turned on here.
    private bool WholeNetwork() => AllowInbound && (!_baseAllowInbound || _inboundNetwork);

    // Toggle / combo autosave: fire-and-forget the serialized commit.
    private void FireAutoSave()
    {
        if (AutoSave)
        {
            _ = AutoSaveAsync();
        }
    }

    // Serialized autosave for the open config, shared by toggle changes and the field-blur handler. A change that
    // arrives mid-commit re-runs once the in-flight commit settles, and the baseline is blessed only when the sent
    // state is still current - so a value changed during the commit is resent, not silently marked clean.
    public async Task AutoSaveAsync()
    {
        if (_applying)
        {
            return;
        }

        if (_committing)
        {
            _commitPending = true;
            return;
        }

        _committing = true;
        try
        {
            do
            {
                _commitPending = false;
                if (!IsDirty)
                {
                    break;
                }

                if (await CommitAsync() && !_commitPending)
                {
                    CaptureBaseline();
                }
            }
            while (_commitPending);
        }
        finally
        {
            _committing = false;
        }
    }

    // Drops a single-host prefix, which says nothing next to the address itself.
    private static string FormatAddresses(string addresses)
    {
        var parts = addresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.EndsWith("/32", StringComparison.Ordinal) || part.EndsWith("/128", StringComparison.Ordinal)
                ? part[..part.LastIndexOf('/')]
                : part);
        return string.Join(", ", parts);
    }
}
