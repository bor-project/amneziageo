using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// A single configuration row in the list.
/// </summary>
internal sealed partial class ConfigItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private bool _geoSplit;

    [ObservableProperty]
    private IReadOnlyList<string> _rules = [];

    [ObservableProperty]
    private bool _useWebSocket;

    [ObservableProperty]
    private string _webSocketHost = string.Empty;

    [ObservableProperty]
    private int _webSocketPort = 443;

    [ObservableProperty]
    private string _dns = string.Empty;

    [ObservableProperty]
    private string _exclusions = string.Empty;

    [ObservableProperty]
    private int _mtu;

    [ObservableProperty]
    private bool _useIpv6;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(DotBrush))]
    [NotifyPropertyChangedFor(nameof(RowActionText))]
    private string _status = ConnectionStatus.Idle;

    /// <summary>
    /// The state dot colour. Every row keeps the dot's place, only the configuration the tunnel is bound to
    /// paints it, so a row never shifts.
    /// </summary>
    public IBrush DotBrush => Status is ConnectionStatus.Connected
        or ConnectionStatus.Connecting
        or ConnectionStatus.Disconnecting
        ? StatusBrush
        : Brushes.Transparent;

    /// <summary>
    /// What clicking the row does: the running configuration goes down, every other one is dialled.
    /// </summary>
    public string RowActionText => string.Equals(Status, ConnectionStatus.Connected, StringComparison.Ordinal)
        ? Loc.Instance.Get("Main_ServerRowDisconnect")
        : Loc.Instance.Get("Main_ServerRowTooltip");

    /// <summary>
    /// The localized status label.
    /// </summary>
    public string StatusText => StatusLabels.Text(Status);

    /// <summary>
    /// The status badge color.
    /// </summary>
    public IBrush StatusBrush => StatusLabels.Brush(Status);

    // Whether this row is the configuration the connect button dials.
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private ProbeOutcome _probeState = ProbeOutcome.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private int _probeMilliseconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private bool _probing;

    // Seconds since the running tunnel's peer last answered; -1 on every config that is not running.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(LinkSilent))]
    private int _handshakeAgeSeconds = -1;

    // Whether the tunnel reports its own liveness, which beats an echo the server may be refusing to send.
    private bool LinkKnown => HandshakeAgeSeconds >= 0;

    /// <summary>
    /// Whether the running tunnel has heard nothing from this server for longer than a rekey window.
    /// </summary>
    public bool LinkSilent => HandshakeAgeSeconds >= HandshakeAge.SilentSeconds;

    private static readonly IBrush _fast = new SolidColorBrush(Color.FromRgb(0x1F, 0x9D, 0x57));
    private static readonly IBrush _slow = new SolidColorBrush(Color.FromRgb(0xC8, 0x7A, 0x00));
    private static readonly IBrush _dead = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush _idle = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    /// <summary>
    /// The response cell: the round trip, a dash before the first measurement, or why nothing came back.
    /// </summary>
    public string ProbeText => Probing
        ? "..."
        : LinkKnown
        ? (LinkSilent ? Loc.Instance.Get("Main_ProbeNoAnswer") : Loc.Instance.Get("Main_ProbeHandshake", HandshakeAgeSeconds))
        : ProbeState switch
        {
            ProbeOutcome.Alive => Loc.Instance.Get("Main_ProbeMilliseconds", ProbeMilliseconds),
            ProbeOutcome.NoAnswer => Loc.Instance.Get("Main_ProbeNoAnswer"),
            ProbeOutcome.NoAddress => Loc.Instance.Get("Main_ProbeNoAddress"),
            _ => "-",
        };

    /// <summary>
    /// The response colour: green for a quick answer, amber for a slow one, red for none.
    /// </summary>
    public IBrush ProbeBrush => Probing
        ? _idle
        : LinkKnown
        ? (LinkSilent ? _dead : _fast)
        : ProbeState switch
        {
            ProbeOutcome.Alive => ProbeMilliseconds <= 200 ? _fast : _slow,
            ProbeOutcome.Unknown => _idle,
            _ => _dead,
        };
}
