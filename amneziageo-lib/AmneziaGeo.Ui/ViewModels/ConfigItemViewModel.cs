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
    [NotifyPropertyChangedFor(nameof(IsLive))]
    private string _status = ConnectionStatus.Idle;

    // Проходит ли карточка через поиск по каталогу.
    [ObservableProperty]
    private bool _matchesFilter = true;

    /// <summary>
    /// Стоит ли туннель на этой конфигурации: карточка каталога носит об этом метку.
    /// </summary>
    public bool IsLive => string.Equals(Status, ConnectionStatus.Connected, StringComparison.Ordinal);

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
    [NotifyPropertyChangedFor(nameof(ProbeChipBrush))]
    [NotifyPropertyChangedFor(nameof(LinkPingValue))]
    [NotifyPropertyChangedFor(nameof(LinkLossValue))]
    private ProbeOutcome _probeState = ProbeOutcome.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(ProbeChipBrush))]
    [NotifyPropertyChangedFor(nameof(LinkPingValue))]
    [NotifyPropertyChangedFor(nameof(LinkLossValue))]
    private int _probeMilliseconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(ProbeChipBrush))]
    [NotifyPropertyChangedFor(nameof(LinkPingValue))]
    [NotifyPropertyChangedFor(nameof(LinkLossValue))]
    private bool _probing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(ProbeChipBrush))]
    [NotifyPropertyChangedFor(nameof(LinkPingValue))]
    [NotifyPropertyChangedFor(nameof(LinkLossValue))]
    private int _probeLossPercent;

    // Whether this server won the last sweep of them all.
    [ObservableProperty]
    private bool _isBest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkSpeedText))]
    [NotifyPropertyChangedFor(nameof(LinkRxValue))]
    [NotifyPropertyChangedFor(nameof(LinkTxValue))]
    [NotifyPropertyChangedFor(nameof(LinkSpeedUnit))]
    private long _rxBitsPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkSpeedText))]
    [NotifyPropertyChangedFor(nameof(LinkRxValue))]
    [NotifyPropertyChangedFor(nameof(LinkTxValue))]
    [NotifyPropertyChangedFor(nameof(LinkSpeedUnit))]
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
    [NotifyPropertyChangedFor(nameof(ProbeChipBrush))]
    [NotifyPropertyChangedFor(nameof(LinkPingValue))]
    [NotifyPropertyChangedFor(nameof(LinkLossValue))]
    private int _linkLossPercent = LinkHealth.LossUnknown;

    // Round trip the running tunnel timed to its far end; -1 on every config that is not running.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(ProbeChipBrush))]
    [NotifyPropertyChangedFor(nameof(LinkPingValue))]
    [NotifyPropertyChangedFor(nameof(LinkLossValue))]
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
    [NotifyPropertyChangedFor(nameof(ProbeChipBrush))]
    [NotifyPropertyChangedFor(nameof(LinkPingValue))]
    [NotifyPropertyChangedFor(nameof(LinkLossValue))]
    [NotifyPropertyChangedFor(nameof(LinkSilent))]
    [NotifyPropertyChangedFor(nameof(ShowLinkSpeed))]
    [NotifyPropertyChangedFor(nameof(ShowLinkLoss))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(LinkRxValue))]
    [NotifyPropertyChangedFor(nameof(LinkTxValue))]
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
    private static readonly IBrush _fastSoft = new SolidColorBrush(Color.FromArgb(0x24, 0x1F, 0x9D, 0x57));
    private static readonly IBrush _slowSoft = new SolidColorBrush(Color.FromArgb(0x24, 0xC8, 0x7A, 0x00));
    private static readonly IBrush _deadSoft = new SolidColorBrush(Color.FromArgb(0x24, 0xC0, 0x39, 0x2B));
    private static readonly IBrush _idleSoft = new SolidColorBrush(Color.FromArgb(0x1A, 0x80, 0x80, 0x80));

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
    public IBrush ProbeBrush => Tone switch
    {
        ProbeTone.Fast => _fast,
        ProbeTone.Slow => _slow,
        ProbeTone.Dead => _dead,
        _ => _idle,
    };

    /// <summary>
    /// Заливка чипа задержки в каталоге: тот же вердикт, что и у текста, приглушённый под фон.
    /// </summary>
    public IBrush ProbeChipBrush => Tone switch
    {
        ProbeTone.Fast => _fastSoft,
        ProbeTone.Slow => _slowSoft,
        ProbeTone.Dead => _deadSoft,
        _ => _idleSoft,
    };

    // Loss at which a server that still answers is no better than one that does not.
    private const int LossyPercent = 40;

    // How the last answer reads. The running tunnel's own time stands ahead of the echo to the endpoint, and a
    // server that answers is green only when it is both quick and lossless.
    private ProbeTone Tone => Probing
        ? ProbeTone.Idle
        : LinkSilent
        ? ProbeTone.Dead
        : LinkTimed
        ? (LinkRttMs <= 200 && !LinkLossy ? ProbeTone.Fast : ProbeTone.Slow)
        : ProbeState switch
        {
            ProbeOutcome.Alive => ProbeLossPercent >= LossyPercent
                ? ProbeTone.Dead
                : ProbeMilliseconds <= 200 && ProbeLossPercent == 0 ? ProbeTone.Fast : ProbeTone.Slow,
            ProbeOutcome.Unknown => ProbeTone.Idle,
            _ => LinkKnown ? ProbeTone.Idle : ProbeTone.Dead,
        };

    // Стоит вместо числа, которого нет.
    private const string Absent = "—";

    /// <summary>
    /// What the tunnel receives, as a bare number for a tile; the tile beside it carries the unit.
    /// </summary>
    public string LinkRxValue => LinkKnown ? SpeedFormat.Value(RxBitsPerSecond, TxBitsPerSecond) : Absent;

    /// <summary>
    /// What the tunnel sends, as a bare number for a tile.
    /// </summary>
    public string LinkTxValue => LinkKnown ? SpeedFormat.Value(TxBitsPerSecond, RxBitsPerSecond) : Absent;

    /// <summary>
    /// The unit both directions are written in.
    /// </summary>
    public string LinkSpeedUnit => SpeedFormat.Unit(RxBitsPerSecond, TxBitsPerSecond);

    /// <summary>
    /// Round trip in milliseconds: the running tunnel's own time, and the echo to the endpoint on every
    /// configuration that is not running.
    /// </summary>
    public string LinkPingValue => LinkTimed
        ? LinkRttMs.ToString()
        : ProbeState == ProbeOutcome.Alive ? ProbeMilliseconds.ToString() : Absent;

    /// <summary>
    /// The share of probes lost, from the same source as the time beside it.
    /// </summary>
    public string LinkLossValue => LinkHealth.LossKnown(LinkLossPercent)
        ? LinkLossPercent.ToString()
        : ProbeState == ProbeOutcome.Alive ? ProbeLossPercent.ToString() : Absent;
}

/// <summary>
/// How the last measurement reads.
/// </summary>
internal enum ProbeTone
{
    /// <summary>
    /// Nothing to read yet.
    /// </summary>
    Idle,

    /// <summary>
    /// Quick and lossless.
    /// </summary>
    Fast,

    /// <summary>
    /// Slow or lossy.
    /// </summary>
    Slow,

    /// <summary>
    /// No use at all.
    /// </summary>
    Dead,
}
