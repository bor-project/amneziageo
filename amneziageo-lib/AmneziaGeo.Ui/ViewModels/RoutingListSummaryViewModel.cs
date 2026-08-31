using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// A summary row for a shared routing list as seen on the main page.
/// </summary>
internal sealed partial class RoutingListSummaryViewModel : ViewModelBase
{
    private readonly ObservableCollection<CardTag> _tags = [];

    private AsyncRelayCommand? _toggleGlobal;
    private AsyncRelayCommand? _toggleUdp;

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
    /// Сохранение настроек списка: ставит владелец каталога, своей связи с агентом у строки нет.
    /// </summary>
    public Func<RoutingListSummaryViewModel, Task<bool>>? SaveSettings { get; set; }

    /// <summary>
    /// Переключает полный туннель с плашки карточки.
    /// </summary>
    public IAsyncRelayCommand ToggleGlobalCommand =>
        _toggleGlobal ??= new AsyncRelayCommand(() => ToggleAsync(() => UseGlobalProxy = !UseGlobalProxy));

    /// <summary>
    /// Переключает весь UDP с плашки карточки.
    /// </summary>
    public IAsyncRelayCommand ToggleUdpCommand =>
        _toggleUdp ??= new AsyncRelayCommand(() => ToggleAsync(() => AllUdp = !AllUdp));

    /// <summary>
    /// Mode labels on the card: everything through the tunnel and all UDP.
    /// </summary>
    public IReadOnlyList<CardTag> Tags
    {
        get
        {
            CardTag.Sync(_tags,
            [
                new(Loc.Instance.Get("Main_CardTagGlobal"), UseGlobalProxy, ToggleGlobalCommand),
                new(Loc.Instance.Get("Main_CardTagUdp"), AllUdp, ToggleUdpCommand),
            ]);
            return _tags;
        }
    }

    // Переворачивает режим и отправляет строку; отказ агента возвращает плашку на место.
    private async Task ToggleAsync(Action flip)
    {
        flip();
        if (SaveSettings is not { } save)
        {
            return;
        }

        if (!await save(this))
        {
            flip();
        }
    }

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
        OnPropertyChanged(nameof(ProxyRulesText));
        OnPropertyChanged(nameof(DirectRulesText));
        OnPropertyChanged(nameof(BlockRulesText));
        OnPropertyChanged(nameof(Tags));
    }
}
