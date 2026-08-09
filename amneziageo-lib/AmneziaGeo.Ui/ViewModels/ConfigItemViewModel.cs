using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    [NotifyPropertyChangedFor(nameof(ShowStatusFrame))]
    [NotifyPropertyChangedFor(nameof(ShowSelectedFrame))]
    [NotifyPropertyChangedFor(nameof(RowActionText))]
    private string _status = ConnectionStatus.Idle;

    /// <summary>
    /// Whether the row wears its frame in the connection colour: the configuration the tunnel is bound to.
    /// </summary>
    public bool ShowStatusFrame => Status is ConnectionStatus.Connected
        or ConnectionStatus.Connecting
        or ConnectionStatus.Disconnecting;

    /// <summary>
    /// Whether the row wears the grey frame: the configuration the user picked, with no tunnel on it.
    /// </summary>
    public bool ShowSelectedFrame => IsSelected && !ShowStatusFrame;

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
    /// The status badge color: a running configuration whose peer has gone quiet wears the switching colour and
    /// not the connected one, like the connect control it stands under.
    /// </summary>
    public IBrush StatusBrush => StatusLabels.Brush(LinkSilent ? ConnectionStatus.Connecting : Status);

    // Whether the user picked this row.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectedFrame))]
    private bool _isSelected;

    // Whether the swipe uncovered the row's edit and delete buttons.
    [ObservableProperty]
    private bool _swipeOpen;

    // Whether the swipe uncovered the row's connect button.
    [ObservableProperty]
    private bool _connectOpen;

    // Whether the row's delete is armed and waiting for the confirm.
    [ObservableProperty]
    private bool _deletePending;

    // Covering the buttons disarms the delete.
    partial void OnSwipeOpenChanged(bool value)
    {
        if (!value)
        {
            DeletePending = false;
        }
    }

    /// <summary>
    /// Заряжает удаление строки: пара «Подтвердить / Отмена» занимает место кнопок.
    /// </summary>
    [RelayCommand]
    private void RequestDelete() => DeletePending = true;

    /// <summary>
    /// Снимает подтверждение удаления.
    /// </summary>
    [RelayCommand]
    private void CancelDelete() => DeletePending = false;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private int _probeLossPercent;

    // Whether this server won the last sweep of them all.
    [ObservableProperty]
    private bool _isBest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkSpeedText))]
    private long _rxBitsPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkSpeedText))]
    private long _txBitsPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkChurning))]
    [NotifyPropertyChangedFor(nameof(LinkChurnText))]
    private int _handshakesPerMinute;

    /// <summary>
    /// Whether the tunnel keeps re-establishing its session instead of carrying traffic.
    /// </summary>
    public bool LinkChurning => LinkHealth.Churning(HandshakesPerMinute);

    /// <summary>
    /// How many sessions a minute the link burns; empty while it holds one.
    /// </summary>
    public string LinkChurnText => LinkChurning
        ? Loc.Instance.Get("Main_LinkChurn", HandshakesPerMinute)
        : string.Empty;

    /// <summary>
    /// What the running tunnel carries in both directions.
    /// </summary>
    public string LinkSpeedText => SpeedFormat.Pair(RxBitsPerSecond, TxBitsPerSecond);

    // Seconds since the running tunnel's peer last answered; -1 on every config that is not running.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(LinkSilent))]
    [NotifyPropertyChangedFor(nameof(ShowLinkSpeed))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private int _handshakeAgeSeconds = -1;

    /// <summary>
    /// Whether the row carries a live throughput reading, which only the running tunnel does.
    /// </summary>
    public bool ShowLinkSpeed => LinkKnown;

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
    /// The response cell: the round trip, a dash before the first measurement, or why nothing came back. The
    /// running tunnel overrides it: its own silence is the answer, and an echo it swallows leaves the cell empty
    /// instead of calling a live server dead.
    /// </summary>
    public string ProbeText => Probing
        ? "..."
        : LinkSilent
        ? Loc.Instance.Get("Main_ProbeNoAnswer")
        : ProbeState switch
        {
            ProbeOutcome.Alive => ProbeLossPercent > 0
                ? Loc.Instance.Get("Main_ProbeMillisecondsLoss", ProbeMilliseconds, ProbeLossPercent)
                : Loc.Instance.Get("Main_ProbeMilliseconds", ProbeMilliseconds),
            ProbeOutcome.NoAnswer => LinkKnown ? string.Empty : Loc.Instance.Get("Main_ProbeNoAnswer"),
            ProbeOutcome.NoAddress => Loc.Instance.Get("Main_ProbeNoAddress"),
            _ => LinkKnown ? string.Empty : "-",
        };

    /// <summary>
    /// The response colour: green for a quick clean answer, amber for a slow or lossy one, red for none.
    /// </summary>
    public IBrush ProbeBrush => Probing
        ? _idle
        : LinkSilent
        ? _dead
        : ProbeState switch
        {
            ProbeOutcome.Alive => AliveBrush,
            ProbeOutcome.Unknown => _idle,
            _ => LinkKnown ? _idle : _dead,
        };

    // Loss at which a server that still answers is no better than one that does not.
    private const int LossyPercent = 40;

    // A server that answers: green only when it is both quick and lossless.
    private IBrush AliveBrush => ProbeLossPercent >= LossyPercent
        ? _dead
        : ProbeMilliseconds <= 200 && ProbeLossPercent == 0 ? _fast : _slow;
}
