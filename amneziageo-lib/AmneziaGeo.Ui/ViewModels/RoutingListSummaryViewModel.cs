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

    private AsyncRelayCommand? _toggleUdp;

    private AsyncRelayCommand? _update;

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
    [NotifyPropertyChangedFor(nameof(ShowProxyRules))]
    private int _proxyRuleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectRulesText))]
    private int _directRuleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockRulesText))]
    private int _blockRuleCount;

    // Whether the list carries everything instead of its own rules.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    [NotifyPropertyChangedFor(nameof(ShowProxyRules))]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private bool _useGlobalProxy;

    // Whether the list carries all UDP.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private bool _allUdp;

    // The configuration the list arrived with, empty for a list made on the device.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    private string _source = string.Empty;

    // Whether the server of the list holds a newer version.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private bool _hasUpdate;

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
    /// Режим списка строкой под его именем.
    /// </summary>
    public string ModeText => Loc.Instance.Get(UseGlobalProxy ? "Main_VpnModeFull" : "Main_VpnModeSelected");

    /// <summary>
    /// Стоит ли в строке состава счётчик правил туннеля.
    /// </summary>
    public bool ShowProxyRules => !UseGlobalProxy || ProxyRuleCount > 0;

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
    /// Откуда список: имя конфигурации, с которой он приехал, или пометка локального.
    /// </summary>
    public string SourceText => Source.Length > 0 ? Source : Loc.Instance.Get("Main_CardSourceLocal");

    /// <summary>
    /// Сохранение настроек списка: ставит владелец каталога, своей связи с агентом у строки нет.
    /// </summary>
    public Func<RoutingListSummaryViewModel, Task<bool>>? SaveSettings { get; set; }

    /// <summary>
    /// Приём новой версии списка с сервера: ставит владелец каталога.
    /// </summary>
    public Func<RoutingListSummaryViewModel, Task>? TakeUpdate { get; set; }

    /// <summary>
    /// Берёт новую версию списка с плашки карточки.
    /// </summary>
    public IAsyncRelayCommand UpdateCommand =>
        _update ??= new AsyncRelayCommand(() => TakeUpdate?.Invoke(this) ?? Task.CompletedTask);

    /// <summary>
    /// Переключает весь UDP с плашки карточки.
    /// </summary>
    public IAsyncRelayCommand ToggleUdpCommand =>
        _toggleUdp ??= new AsyncRelayCommand(() => ToggleAsync(() => AllUdp = !AllUdp));

    /// <summary>
    /// Плашки карточки: весь UDP у списка, который несёт только выбранное, и обновление, когда сервер отдал новую
    /// версию.
    /// </summary>
    public IReadOnlyList<CardTag> Tags
    {
        get
        {
            var wanted = new List<CardTag>();
            if (!UseGlobalProxy)
            {
                wanted.Add(new(Loc.Instance.Get("Main_CardTagUdp"), AllUdp, ToggleUdpCommand));
            }

            if (HasUpdate)
            {
                wanted.Add(new(Loc.Instance.Get("Main_CardTagUpdate"), true, UpdateCommand, warn: true));
            }

            CardTag.Sync(_tags, wanted);
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
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ProxyRulesText));
        OnPropertyChanged(nameof(DirectRulesText));
        OnPropertyChanged(nameof(BlockRulesText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(Tags));
    }
}
