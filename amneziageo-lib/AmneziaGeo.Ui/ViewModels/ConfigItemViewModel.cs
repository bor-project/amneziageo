using Avalonia;
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
    [NotifyPropertyChangedFor(nameof(ShowStatusFrame))]
    [NotifyPropertyChangedFor(nameof(CardActionText))]
    [NotifyPropertyChangedFor(nameof(CardBusy))]
    [NotifyPropertyChangedFor(nameof(CardPowerBrush))]
    [NotifyPropertyChangedFor(nameof(CardPowerBorderBrush))]
    [NotifyPropertyChangedFor(nameof(CardPowerForeground))]
    [NotifyPropertyChangedFor(nameof(LinkSilent))]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(CardLossText))]
    [NotifyPropertyChangedFor(nameof(CardLossBrush))]
    private string _status = ConnectionStatus.Idle;

    /// <summary>
    /// Whether the row wears its frame in the connection colour: the configuration the tunnel is bound to.
    /// </summary>
    public bool ShowStatusFrame => Status is ConnectionStatus.Connected
        or ConnectionStatus.Connecting
        or ConnectionStatus.Disconnecting;

    /// <summary>
    /// Действие кнопки карточки словом: со своей конфигурации карточка туннель снимает, на всякой другой -
    /// поднимает.
    /// </summary>
    public string CardActionText =>
        Loc.Instance.Get(ShowStatusFrame ? "Main_DisconnectNowLink" : "Main_ConnectNowLink");

    /// <summary>
    /// Носит ли карточка свою рамку поверх общей: та, которую выбрали в каталоге. Рамка стоит на цели, пока
    /// её не увели, и уводит цель за собой, когда туннеля нет.
    /// </summary>
    public bool ShowCardFrame => IsPicked;

    /// <summary>
    /// Крутит ли круг дугу вместо значка: туннель на карточке поднимается или падает.
    /// </summary>
    public bool CardBusy => PowerState == 1;

    /// <summary>
    /// Заливка круга подключения: синяя у работающего туннеля, белая у остальных.
    /// </summary>
    public IBrush CardPowerBrush => PowerState == 2 && !PowerSilent ? _powerBlue : Brushes.White;

    /// <summary>
    /// Обводка круга: цвет переключения на переходе и у замолчавшего сервера, серая у выключенного.
    /// </summary>
    public IBrush CardPowerBorderBrush => PowerSilent ? _powerAmber : PowerState switch
    {
        2 => Brushes.Transparent,
        1 => _powerAmber,
        _ => _powerRing,
    };

    /// <summary>
    /// Значок и дуга: белые у работающего туннеля, цвета переключения на переходе, серые у выключенного.
    /// </summary>
    public IBrush CardPowerForeground => PowerSilent ? _powerAmber : PowerState switch
    {
        2 => Brushes.White,
        1 => _powerAmber,
        _ => _powerGlyph,
    };

    // 0 - выключен, 1 - переход, 2 - подключено.
    private int PowerState => Status switch
    {
        ConnectionStatus.Connected => 2,
        ConnectionStatus.Connecting or ConnectionStatus.Disconnecting => 1,
        _ => 0,
    };

    // Туннель поднят, а сервер молчит: круг носит цвет переключения.
    private bool PowerSilent => PowerState == 2 && LinkSilent;

    // Whether the tunnel is set to use this configuration.
    [ObservableProperty]
    private bool _isSelected;

    // Whether the card is the one picked in the catalogue.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCardFrame))]
    private bool _isPicked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(CardLossText))]
    [NotifyPropertyChangedFor(nameof(CardLossBrush))]
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
    [NotifyPropertyChangedFor(nameof(CardLossText))]
    [NotifyPropertyChangedFor(nameof(CardLossBrush))]
    private int _probeLossPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkSpeedText))]
    [NotifyPropertyChangedFor(nameof(CardSpeedText))]
    private long _rxBitsPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkSpeedText))]
    [NotifyPropertyChangedFor(nameof(CardSpeedText))]
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
    /// What the running tunnel carries in both directions; names the absence while nothing runs.
    /// </summary>
    public string LinkSpeedText => LinkKnown
        ? SpeedFormat.Pair(RxBitsPerSecond, TxBitsPerSecond)
        : Loc.Instance.Get("Main_LinkSpeedUnknown");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkLossText))]
    [NotifyPropertyChangedFor(nameof(LinkLossy))]
    [NotifyPropertyChangedFor(nameof(LinkLossBrush))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(CardLossText))]
    [NotifyPropertyChangedFor(nameof(CardLossBrush))]
    private int _linkLossPercent = LinkHealth.LossUnknown;

    // Round trip the running tunnel timed to its far end; -1 on every config that is not running.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private int _linkRttMs = -1;

    /// <summary>
    /// Whether the running tunnel drops enough for it to be felt.
    /// </summary>
    public bool LinkLossy => LinkSteady && LinkHealth.Lossy(LinkLossPercent);

    /// <summary>
    /// The share of the tunnel's own probes that never came back; names the absence while nothing has answered.
    /// </summary>
    public string LinkLossText => LinkSteady && LinkHealth.LossKnown(LinkLossPercent)
        ? Loc.Instance.Get("Main_LinkLoss", LinkLossPercent)
        : Loc.Instance.Get("Main_LinkLossUnknown");

    /// <summary>
    /// Colour of the loss on the card: muted while the link is clean, the warning colour once it drops enough
    /// to be felt.
    /// </summary>
    public IBrush LinkLossBrush => LinkLossy ? _slow : _idle;

    /// <summary>
    /// Что несёт туннель на карточке: обе скорости коротко, «N/A» - пока туннеля нет.
    /// </summary>
    public string CardSpeedText => LinkKnown
        ? SpeedFormat.Compact(RxBitsPerSecond, TxBitsPerSecond)
        : Loc.Instance.Get("Main_ProbeUnknown");

    /// <summary>
    /// Потери на карточке: свои у работающего туннеля, замеренные эхом у остальных, «N/A» - пока мерить было
    /// нечего.
    /// </summary>
    public string CardLossText => CardLossKnown
        ? Loc.Instance.Get("Main_CardLoss", CardLossPercent)
        : Loc.Instance.Get("Main_ProbeUnknown");

    /// <summary>
    /// Цвет потерь на карточке: серый у чистого канала, предупреждающий - у ощутимых потерь.
    /// </summary>
    public IBrush CardLossBrush => CardLossKnown && LinkHealth.Lossy(CardLossPercent) ? _slow : _idle;

    // Потери, за которые отвечает работающий туннель.
    private bool TunnelLossKnown => LinkSteady && LinkHealth.LossKnown(LinkLossPercent);

    // Есть ли что показать в потерях: туннельное показание либо ответивший замер.
    private bool CardLossKnown => TunnelLossKnown || ProbeState == ProbeOutcome.Alive;

    // Показание потерь: туннельное впереди замеренного эхом.
    private int CardLossPercent => TunnelLossKnown ? LinkLossPercent : ProbeLossPercent;

    /// <summary>
    /// Whether the throughput reading is shown. Android keeps none: the tunnel there counts what its own relay
    /// carries, which is a share of the traffic and no measure of the link.
    /// </summary>
    public static bool SpeedShown => !OperatingSystem.IsAndroid();

    // Seconds since the running tunnel's peer last answered; -1 on every config that is not running.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(LinkSilent))]
    [NotifyPropertyChangedFor(nameof(LinkSpeedText))]
    [NotifyPropertyChangedFor(nameof(LinkLossText))]
    [NotifyPropertyChangedFor(nameof(LinkLossy))]
    [NotifyPropertyChangedFor(nameof(LinkLossBrush))]
    [NotifyPropertyChangedFor(nameof(CardSpeedText))]
    [NotifyPropertyChangedFor(nameof(CardLossText))]
    [NotifyPropertyChangedFor(nameof(CardLossBrush))]
    [NotifyPropertyChangedFor(nameof(CardPowerBrush))]
    [NotifyPropertyChangedFor(nameof(CardPowerBorderBrush))]
    [NotifyPropertyChangedFor(nameof(CardPowerForeground))]
    private int _handshakeAgeSeconds = -1;

    /// <summary>
    /// Whether the row carries a throughput reading at all; the numbers themselves wait for a running tunnel.
    /// </summary>
    public bool ShowLinkSpeed => SpeedShown;

    // Whether the running tunnel has timed its own echo. It stands ahead of the echo to the endpoint: that one
    // travels the tunnel it is measuring once the session takes the default route, and the answer it never gets
    // would leave the running server the only card on the screen with no time on it.
    private bool LinkTimed => LinkKnown && LinkRttMs >= 0;

    // Whether the tunnel reports its own liveness, which beats an echo the server may be refusing to send.
    private bool LinkKnown => HandshakeAgeSeconds >= 0;

    // Показания, за которые отвечает канал: туннель на подъёме и на сносе теряет свои же пробы на перестройке
    // маршрутов, и потери тех секунд к серверу отношения не имеют.
    private bool LinkSteady => LinkKnown && !Transitioning;

    /// <summary>
    /// Whether the running tunnel has heard nothing from this server for longer than a rekey window.
    /// </summary>
    public bool LinkSilent => !Transitioning && HandshakeAgeSeconds >= HandshakeAge.SilentSeconds;

    // Туннель поднимается или падает: его показания к серверу отношения не имеют.
    private bool Transitioning => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting;

    // Цвета круга подключения: те же, что у кнопки в шапке.
    private static readonly IBrush _powerBlue = new SolidColorBrush(Color.FromRgb(0x2A, 0x6F, 0xDB));
    private static readonly IBrush _powerAmber = new SolidColorBrush(Color.FromRgb(0xE0, 0x90, 0x2F));
    private static readonly IBrush _powerRing = new SolidColorBrush(Color.FromRgb(0xD9, 0xDD, 0xE6));
    private static readonly IBrush _powerGlyph = new SolidColorBrush(Color.FromRgb(0x7B, 0x81, 0x8D));

    private static readonly IBrush _fast = new SolidColorBrush(Color.FromRgb(0x2A, 0x6F, 0xDB));
    private static readonly IBrush _slow = new SolidColorBrush(Color.FromRgb(0xC8, 0x7A, 0x00));
    private static readonly IBrush _dead = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush _idle = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    /// <summary>
    /// Отклик сервера: время последнего замера, «нет ответа» у потерянной связи и «N/A», пока мерить было
    /// нечего. Замер живёт отдельно от туннеля и подключением не сбрасывается.
    /// </summary>
    public string ProbeText => Probing
        ? "..."
        : HasProbeTime
        ? RoundTripText
        : ProbeUnreachable
        ? Loc.Instance.Get(ProbeState == ProbeOutcome.NoAddress ? "Main_ProbeNoAddress" : "Main_ProbeNoAnswer")
        : Loc.Instance.Get("Main_ProbeUnknown");

    /// <summary>
    /// Цвет отклика, он же у оценки рядом: красный только у потерянной связи, серый - пока данных нет.
    /// </summary>
    public IBrush ProbeBrush => Probing
        ? _idle
        : HasProbeTime
        ? VerdictBrush
        : ProbeUnreachable
        ? _dead
        : _idle;

    // Время замера: потери стоят своим полем рядом.
    private string RoundTripText => Loc.Instance.Get("Main_ProbeMilliseconds", ProbeRoundTrip);

    // Время, которое стоит на карточке: своё у работающего туннеля, иначе последний замер эндпоинта.
    private int ProbeRoundTrip => LinkTimed ? LinkRttMs : ProbeMilliseconds;

    // Есть ли что показать: идущий замер и молчащий туннель времени не дают.
    private bool HasProbeTime => !Probing && !LinkSilent && (LinkTimed || ProbeState == ProbeOutcome.Alive);

    // Связь потеряна на самом деле: туннель замолчал либо эхо к серверу осталось без ответа. Отсутствие
    // замера - другое состояние и красным не показывается.
    private bool ProbeUnreachable => !Transitioning
        && (LinkSilent || (!LinkKnown && ProbeState is ProbeOutcome.NoAnswer or ProbeOutcome.NoAddress));

    // Отклик, который уже чувствуется: потери или время сверх границы.
    private bool ProbeSlow => ProbeLossy || ProbeRoundTrip > SteadyMs;

    // Потери, из-за которых ответ перестаёт считаться хорошим.
    private bool ProbeLossy => LinkTimed ? LinkLossy : ProbeLossPercent >= LossyPercent;

    // Один цвет на число отклика и его оценку.
    private IBrush VerdictBrush => ProbeSlow ? _slow : _fast;

    // Граница оценки отклика.
    private const int SteadyMs = 200;

    // Loss at which a server that still answers is no better than one that does not.
    private const int LossyPercent = 40;
}
