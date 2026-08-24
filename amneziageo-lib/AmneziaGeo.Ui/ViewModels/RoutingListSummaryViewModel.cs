using Avalonia;
using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// A summary row for a shared routing list as seen on the main page.
/// </summary>
internal sealed partial class RoutingListSummaryViewModel : ViewModelBase
{
    [ObservableProperty]
    private long _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _ruleCount;

    [ObservableProperty]
    private int _routeCount;

    [ObservableProperty]
    private int _domainCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyRulesText))]
    private int _proxyRuleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectRulesText))]
    private int _directRuleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockRulesText))]
    private int _blockRuleCount;

    // Whether the list carries everything instead of its own rules.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private bool _useGlobalProxy;

    // Whether the list carries all UDP.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private bool _allUdp;

    [ObservableProperty]
    private bool _isSelected;

    // Сколько конфигураций ходит по этому списку.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsedByText))]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private int _usedByCount;

    // Whether the tunnel runs on this list, so its rules are the ones in force.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardFrameBrush))]
    private bool _isLive;

    // Whether the card is the one picked in the catalogue.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCardFrame))]
    private bool _isPicked;

    /// <summary>
    /// Сколько правил уводит в туннель.
    /// </summary>
    public string ProxyRulesText => Loc.Instance.Get("Main_CardRulesProxy", ProxyRuleCount);

    /// <summary>
    /// Сколько правил пускает мимо туннеля.
    /// </summary>
    public string DirectRulesText => Loc.Instance.Get("Main_CardRulesDirect", DirectRuleCount);

    /// <summary>
    /// Сколько правил глушит обращения.
    /// </summary>
    public string BlockRulesText => Loc.Instance.Get("Main_CardRulesBlock", BlockRuleCount);

    /// <summary>
    /// Сколько серверов ходит по списку.
    /// </summary>
    public string UsedByText => UsedByCount > 0
        ? Loc.Instance.Get("Main_CardUsedBy", UsedByCount)
        : Loc.Instance.Get("Main_CardUsedByNone");

    /// <summary>
    /// Mode labels on the card: who routes by it, everything through the tunnel and all UDP.
    /// </summary>
    public IReadOnlyList<CardTag> Tags =>
    [
        new(UsedByText, UsedByCount > 0),
        new(Loc.Instance.Get("Main_CardTagGlobal"), UseGlobalProxy),
        new(Loc.Instance.Get("Main_CardTagUdp"), AllUdp),
    ];

    /// <summary>
    /// Носит ли карточка свою рамку поверх общей: та, которую выбрали в каталоге.
    /// </summary>
    public bool ShowCardFrame => IsPicked;

    /// <summary>
    /// Цвет рамки: цвет подключения, пока маршрутизация идёт по этому списку, серый у остальных.
    /// </summary>
    public IBrush CardFrameBrush =>
        StatusLabels.Brush(IsLive ? ConnectionStatus.Connected : ConnectionStatus.Idle);

    /// <summary>
    /// A short human label like "openai · 1 правило · 12 доменов".
    /// </summary>
    public string Detail => Loc.Instance.Get("RoutingSummary_Detail", RuleCount, RouteCount, DomainCount);

    /// <summary>
    /// Re-raises the localized computed labels after a language change.
    /// </summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(UsedByText));
        OnPropertyChanged(nameof(ProxyRulesText));
        OnPropertyChanged(nameof(DirectRulesText));
        OnPropertyChanged(nameof(BlockRulesText));
        OnPropertyChanged(nameof(Tags));
    }
}
