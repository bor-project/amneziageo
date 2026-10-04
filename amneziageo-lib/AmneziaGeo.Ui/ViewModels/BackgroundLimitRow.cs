using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Строка блока «Работа в фоне».
/// </summary>
internal sealed partial class BackgroundLimitRow : ObservableObject
{
    private readonly Action<BackgroundLimitKind> _open;

    /// <summary>
    /// ctor
    /// </summary>
    public BackgroundLimitRow(BackgroundLimitKind kind, BackgroundLimitState state, Action<BackgroundLimitKind> open)
    {
        Kind = kind;
        _state = state;
        _open = open;
    }

    /// <summary>
    /// Ограничение строки.
    /// </summary>
    public BackgroundLimitKind Kind { get; }

    /// <summary>
    /// Состояние ограничения.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(HasState))]
    [NotifyPropertyChangedFor(nameof(IsLimited))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private BackgroundLimitState _state;

    /// <summary>
    /// Имя ограничения.
    /// </summary>
    public string Name => Loc.Instance.Get(NameKey(Kind));

    /// <summary>
    /// Состояние словом.
    /// </summary>
    public string StateText => StateKey(Kind, State) is { Length: > 0 } key ? Loc.Instance.Get(key) : string.Empty;

    /// <summary>
    /// Известно ли состояние.
    /// </summary>
    public bool HasState => State != BackgroundLimitState.Unknown;

    /// <summary>
    /// Ограничивает ли система приложение.
    /// </summary>
    public bool IsLimited => State == BackgroundLimitState.Limited;

    /// <summary>
    /// Подпись кнопки.
    /// </summary>
    public string ActionText => Loc.Instance.Get(ActionKey(Kind, State));

    /// <summary>
    /// Перечитывает подписи на новом языке.
    /// </summary>
    public void Relabel() => OnPropertyChanged(string.Empty);

    /// <summary>
    /// Ключ имени ограничения.
    /// </summary>
    internal static string NameKey(BackgroundLimitKind kind) => kind switch
    {
        BackgroundLimitKind.Notifications => "Background_Notifications",
        BackgroundLimitKind.Battery => "Background_Battery",
        _ => "Background_Autostart",
    };

    /// <summary>
    /// Ключ слова состояния, пустой для неизвестного.
    /// </summary>
    internal static string StateKey(BackgroundLimitKind kind, BackgroundLimitState state) => (kind, state) switch
    {
        (_, BackgroundLimitState.Unknown) => string.Empty,
        (BackgroundLimitKind.Notifications, BackgroundLimitState.Limited) => "Background_NotificationsOff",
        (BackgroundLimitKind.Notifications, _) => "Background_NotificationsOn",
        (BackgroundLimitKind.Battery, BackgroundLimitState.Limited) => "Background_BatteryLimits",
        (BackgroundLimitKind.Battery, _) => "Background_BatteryFree",
        (_, BackgroundLimitState.Limited) => "Background_AutostartOff",
        _ => "Background_AutostartOn",
    };

    /// <summary>
    /// Ключ подписи кнопки.
    /// </summary>
    internal static string ActionKey(BackgroundLimitKind kind, BackgroundLimitState state) => (kind, state) switch
    {
        (BackgroundLimitKind.Notifications, BackgroundLimitState.Limited) => "Background_Allow",
        (BackgroundLimitKind.Autostart, _) => "Background_Open",
        _ => "Background_Configure",
    };

    [RelayCommand]
    private void Open() => _open(Kind);
}
