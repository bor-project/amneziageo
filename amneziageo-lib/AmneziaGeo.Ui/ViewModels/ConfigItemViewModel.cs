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
    [NotifyPropertyChangedFor(nameof(ShowOpenFrame))]
    [NotifyPropertyChangedFor(nameof(RowActionText))]
    private string _status = ConnectionStatus.Idle;

    /// <summary>
    /// Носит ли карточка рамку в цвете состояния: выбранная конфигурация - та, на которую встанет подключение, -
    /// и та, на которой туннель уже стоит.
    /// </summary>
    public bool ShowStatusFrame => IsSelected || Status is ConnectionStatus.Connected
        or ConnectionStatus.Connecting
        or ConnectionStatus.Disconnecting;

    /// <summary>
    /// Носит ли карточка серую рамку: открытая справа конфигурация, пока она не выбрана - у выбранной рамка
    /// в цвете состояния.
    /// </summary>
    public bool ShowOpenFrame => IsOpen && !ShowStatusFrame;

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

    // Выбрана ли эта конфигурация: на неё встанет подключение.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusFrame))]
    [NotifyPropertyChangedFor(nameof(ShowOpenFrame))]
    private bool _isSelected;

    // Открыта ли эта конфигурация в настройках справа.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenFrame))]
    private bool _isOpen;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkLossText))]
    [NotifyPropertyChangedFor(nameof(LinkLossy))]
    [NotifyPropertyChangedFor(nameof(LinkLossBrush))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private int _linkLossPercent = LinkHealth.LossUnknown;

    // Round trip the running tunnel timed to its far end; -1 on every config that is not running.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private int _linkRttMs = -1;

    /// <summary>
    /// Whether the row carries a loss line: the running tunnel carries one from the moment it is up, so the line
    /// no longer appears out of nowhere once the probe window fills.
    /// </summary>
    public bool ShowLinkLoss => LinkKnown;

    /// <summary>
    /// Whether the running tunnel drops enough for it to be felt.
    /// </summary>
    public bool LinkLossy => LinkHealth.Lossy(LinkLossPercent);

    /// <summary>
    /// The share of the tunnel's own probes that never came back; names the absence while nothing has answered.
    /// </summary>
    public string LinkLossText => LinkHealth.LossKnown(LinkLossPercent)
        ? Loc.Instance.Get("Main_LinkLoss", LinkLossPercent)
        : Loc.Instance.Get("Main_LinkLossUnknown");

    /// <summary>
    /// Colour of the loss on the card: muted while the link is clean, the warning colour once it drops enough
    /// to be felt.
    /// </summary>
    public IBrush LinkLossBrush => LinkLossy ? _slow : _idle;

    /// <summary>
    /// Whether the card keeps the throughput line in place while the tunnel is down, so the cards in the
    /// catalogue stand the same height.
    /// </summary>
    public bool ShowLinkSpeedRow => SpeedShown;

    /// <summary>
    /// Whether the card keeps the loss line in place. A television keeps neither: it shows one application at a
    /// time, so nobody is there to read these numbers while the traffic they describe is being made.
    /// </summary>
    public bool ShowLinkLossRow => LinkShown;

    /// <summary>
    /// Whether this device shows what the tunnel carries and loses at all.
    /// </summary>
    public static bool LinkShown => !UiPlatform.IsTelevision;

    /// <summary>
    /// Whether the throughput reading is shown. Android keeps none: the tunnel there counts what its own relay
    /// carries, which is a share of the traffic and no measure of the link.
    /// </summary>
    public static bool SpeedShown => LinkShown && !OperatingSystem.IsAndroid();

    // Seconds since the running tunnel's peer last answered; -1 on every config that is not running.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(LinkSilent))]
    [NotifyPropertyChangedFor(nameof(ShowLinkSpeed))]
    [NotifyPropertyChangedFor(nameof(ShowLinkLoss))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private int _handshakeAgeSeconds = -1;

    /// <summary>
    /// Whether the row carries a live throughput reading, which only the running tunnel does.
    /// </summary>
    public bool ShowLinkSpeed => LinkKnown && SpeedShown;

    // Whether the running tunnel has timed its own echo. It stands ahead of the echo to the endpoint: that one
    // travels the tunnel it is measuring once the session takes the default route, and the answer it never gets
    // would leave the running server the only card on the screen with no time on it.
    private bool LinkTimed => LinkKnown && LinkRttMs >= 0;

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
        : LinkTimed
        ? Loc.Instance.Get("Main_ProbeMilliseconds", LinkRttMs)
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
        : LinkTimed
        ? LinkBrush
        : ProbeState switch
        {
            ProbeOutcome.Alive => AliveBrush,
            ProbeOutcome.Unknown => _idle,
            _ => LinkKnown ? _idle : _dead,
        };

    // Loss at which a server that still answers is no better than one that does not.
    private const int LossyPercent = 40;

    // The running tunnel's own time: green while it is quick and drops nothing worth feeling.
    private IBrush LinkBrush => LinkRttMs <= 200 && !LinkLossy ? _fast : _slow;

    // A server that answers: green only when it is both quick and lossless.
    private IBrush AliveBrush => ProbeLossPercent >= LossyPercent
        ? _dead
        : ProbeMilliseconds <= 200 && ProbeLossPercent == 0 ? _fast : _slow;
}
