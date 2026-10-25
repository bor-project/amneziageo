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
    public BackgroundLimitRow(BackgroundLimitKind kind, BackgroundLimitState state, Action<BackgroundLimitKind> open,
        TunnelNoticeChoice? notice = null)
    {
        Kind = kind;
        _state = state;
        _open = open;
        Notice = kind == BackgroundLimitKind.Notifications ? notice : null;
    }

    /// <summary>
    /// Ограничение строки.
    /// </summary>
    public BackgroundLimitKind Kind { get; }

    /// <summary>
    /// Выбор уведомления туннеля под строкой уведомлений.
    /// </summary>
    public TunnelNoticeChoice? Notice { get; }

    /// <summary>
    /// Стоит ли под строкой выбор уведомления туннеля.
    /// </summary>
    public bool HasNotice => Notice is not null;

    /// <summary>
    /// Состояние ограничения.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(HasState))]
    [NotifyPropertyChangedFor(nameof(IsLimited))]
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
    /// Называет ли строка состояние.
    /// </summary>
    public bool HasState => StateText.Length > 0;

    /// <summary>
    /// Ограничивает ли система приложение.
    /// </summary>
    public bool IsLimited => State == BackgroundLimitState.Limited;

    /// <summary>
    /// Подпись кнопки.
    /// </summary>
    public string ActionText => Loc.Instance.Get("Background_Configure");

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
    /// Ключ слова состояния: есть только у уведомлений.
    /// </summary>
    internal static string StateKey(BackgroundLimitKind kind, BackgroundLimitState state) => (kind, state) switch
    {
        (BackgroundLimitKind.Notifications, BackgroundLimitState.Limited) => "Background_NotificationsOff",
        (BackgroundLimitKind.Notifications, BackgroundLimitState.Free) => "Background_NotificationsOn",
        _ => string.Empty,
    };

    [RelayCommand]
    private void Open() => _open(Kind);
}
