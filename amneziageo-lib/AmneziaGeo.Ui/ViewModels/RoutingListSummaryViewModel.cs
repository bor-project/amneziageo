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

    private RelayCommand? _open;

    private RelayCommand? _openAdvanced;

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
    [NotifyPropertyChangedFor(nameof(FromServer))]
    [NotifyPropertyChangedFor(nameof(ServerMarkText))]
    private string _source = string.Empty;

    // Whether the server of the list holds a newer version.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private bool _hasUpdate;

    // Whether the server keeps the list read-only.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private bool _readOnly;

    // Whether the card warns of a list that sends more than its rules through the VPN.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tags))]
    private bool _warn = true;

    [ObservableProperty]
    private bool _isSelected;

    // The configuration whose server keeps the switch of the list where it stands, empty while none does.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Locked))]
    [NotifyPropertyChangedFor(nameof(ActiveTip))]
    private string _lockedBy = string.Empty;

    // Whether the tunnel runs on this list, so its rules are the ones in force.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardFrameBrush))]
    private bool _isLive;

    // Whether the card is the one picked in the catalogue.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCardFrame))]
    private bool _isPicked;

    /// <summary>
    /// Стоит ли тумблер применения на месте: сервер конфигурации разрешает только свои списки.
    /// </summary>
    public bool Locked => LockedBy.Length > 0;

    /// <summary>
    /// Подсказка тумблера применения.
    /// </summary>
    public string ActiveTip => Locked
        ? Loc.Instance.Get("Main_ServerListsOnlyOf", LockedBy)
        : Loc.Instance.Get("Main_CardActiveFlag");

    /// <summary>
    /// Режим списка строкой под его именем.
    /// </summary>
    public string ModeText => Loc.Instance.Get(UseGlobalProxy ? "Main_VpnModeFull" : "Main_VpnModeSelected");

    /// <summary>
    /// Что список отдаёт туннелю сверх названного в нём.
    /// </summary>
    public ListReach Reach => ReachOf(UseGlobalProxy, AllUdp);

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
    /// Пришёл ли список с сервера AmneziaGeo.
    /// </summary>
    public bool FromServer => Source.Length > 0;

    /// <summary>
    /// Что окно знака говорит о списке с сервера.
    /// </summary>
    public string ServerMarkText => Loc.Instance.Get("Main_ServerMarkList", Source);

    /// <summary>
    /// Открытие списка: ставит владелец каталога.
    /// </summary>
    public Action<RoutingListSummaryViewModel>? Open { get; set; }

    /// <summary>
    /// Открытие списка сразу в «Дополнительно»: ставит владелец каталога.
    /// </summary>
    public Action<RoutingListSummaryViewModel>? OpenAdvanced { get; set; }

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
    /// Открывает список с плашки предупреждения.
    /// </summary>
    public IRelayCommand OpenCommand => _open ??= new RelayCommand(() => Open?.Invoke(this));

    /// <summary>
    /// Открывает «Дополнительно» списка с плашки предупреждения UDP.
    /// </summary>
    public IRelayCommand OpenAdvancedCommand => _openAdvanced ??= new RelayCommand(() => OpenAdvanced?.Invoke(this));

    /// <summary>
    /// Несёт ли устройство весь UDP через туннель.
    /// </summary>
    public static bool AllUdpSupported => !OperatingSystem.IsAndroid() || OperatingSystem.IsAndroidVersionAtLeast(29);

    /// <summary>
    /// Что отдаёт туннелю список с таким режимом и UDP: весь UDP считается там, где устройство его несёт.
    /// </summary>
    public static ListReach ReachOf(bool useGlobalProxy, bool allUdp) =>
        useGlobalProxy ? ListReach.AllApps : allUdp && AllUdpSupported ? ListReach.AllUdp : ListReach.Named;

    /// <summary>
    /// Плашки карточки: предупреждение у списка, который ведёт через VPN все приложения или весь их UDP (все
    /// приложения открывают настройки списка, весь UDP - его «Дополнительно»), только чтение у списка, который
    /// сервер не даёт править, и обновление, когда сервер отдал новую версию.
    /// </summary>
    public IReadOnlyList<CardTag> Tags
    {
        get
        {
            var wanted = new List<CardTag>();
            if (Warn && Reach is not ListReach.Named)
            {
                var allUdp = Reach is ListReach.AllUdp;
                var text = allUdp ? "Main_CardTagAllUdp" : "Main_CardTagAllApps";
                wanted.Add(new(Loc.Instance.Get(text), true, allUdp ? OpenAdvancedCommand : OpenCommand, warn: true, alert: true));
            }

            if (ReadOnly)
            {
                wanted.Add(new(Loc.Instance.Get("Main_CardTagReadOnly"), false, plain: true));
            }

            if (HasUpdate)
            {
                wanted.Add(new(Loc.Instance.Get("Main_CardTagUpdate"), true, UpdateCommand, warn: true));
            }

            CardTag.Sync(_tags, wanted);
            return _tags;
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
        OnPropertyChanged(nameof(ActiveTip));
        OnPropertyChanged(nameof(Tags));
    }
}
