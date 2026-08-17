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

    // Правила по корзинам: карточка каталога показывает их числом с точкой в цвете корзины.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyText))]
    [NotifyPropertyChangedFor(nameof(ShowProxy))]
    private int _proxyRuleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectText))]
    [NotifyPropertyChangedFor(nameof(ShowDirect))]
    private int _directRuleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockText))]
    [NotifyPropertyChangedFor(nameof(ShowBlock))]
    private int _blockRuleCount;

    // Политика списка: обе метки стоят значками в правом краю карточки.
    [ObservableProperty]
    private bool _allUdp;

    [ObservableProperty]
    private bool _useGlobalProxy;

    // Применён ли этот список: его строка носит рамку в цвете «работает».
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenFrame))]
    private bool _isApplied;

    // Открыт ли этот список в настройках справа.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenFrame))]
    private bool _isOpen;

    /// <summary>
    /// Носит ли строка серую рамку: открытый список, пока он не применён - у применённого рамка цветная.
    /// </summary>
    public bool ShowOpenFrame => IsOpen && !IsApplied;

    /// <summary>
    /// Сколько правил в корзине «Прокси».
    /// </summary>
    public string ProxyText => Loc.Instance.Get("RoutingSummary_ProxyCount", ProxyRuleCount);

    /// <summary>
    /// Сколько правил в корзине «Директ».
    /// </summary>
    public string DirectText => Loc.Instance.Get("RoutingSummary_DirectCount", DirectRuleCount);

    /// <summary>
    /// Сколько правил в корзине «Блок».
    /// </summary>
    public string BlockText => Loc.Instance.Get("RoutingSummary_BlockCount", BlockRuleCount);

    /// <summary>
    /// Стоит ли счёт корзины «Прокси»: пустую корзину карточка не показывает.
    /// </summary>
    public bool ShowProxy => ProxyRuleCount > 0;

    /// <summary>
    /// Стоит ли счёт корзины «Директ».
    /// </summary>
    public bool ShowDirect => DirectRuleCount > 0;

    /// <summary>
    /// Стоит ли счёт корзины «Блок».
    /// </summary>
    public bool ShowBlock => BlockRuleCount > 0;

    /// <summary>
    /// A short human label like "openai · 1 правило · 12 доменов".
    /// </summary>
    public string Detail => Loc.Instance.Get("RoutingSummary_Detail", RuleCount, RouteCount, DomainCount);

    /// <summary>
    /// Re-raises the localized computed label after a language change.
    /// </summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ProxyText));
        OnPropertyChanged(nameof(DirectText));
        OnPropertyChanged(nameof(BlockText));
    }
}
