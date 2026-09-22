using System.Globalization;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Подписка открытой конфигурации: когда её читали и чем она ответила. Обновление уходит командой агенту через
/// переданный делегат.
/// </summary>
internal sealed partial class SubscriptionItemViewModel : ViewModelBase
{
    private readonly Func<SubscriptionItemViewModel, Task> _refresh;

    /// <summary>
    /// ctor
    /// </summary>
    public SubscriptionItemViewModel(Func<SubscriptionItemViewModel, Task> refresh)
    {
        _refresh = refresh;
    }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Updated))]
    private long _checkedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _lastError = string.Empty;

    [ObservableProperty]
    private bool _busy;

    /// <summary>
    /// Заголовок строки: имя профиля с сервера, а без него - имя подписки.
    /// </summary>
    public string Caption => Title.Length > 0 ? Title : Name;

    /// <summary>
    /// Не прочиталась ли она в прошлый раз.
    /// </summary>
    public bool HasError => LastError.Length > 0;

    /// <summary>
    /// Когда подписку читали последний раз; пусто, пока её не читали.
    /// </summary>
    public string Updated => CheckedAt > 0 ? Loc.Instance.Get("Main_SubscriptionUpdated", Moment(CheckedAt)) : string.Empty;

    /// <summary>
    /// Перечитывает подписи после смены языка.
    /// </summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Updated));
        OnPropertyChanged(nameof(Caption));
    }

    [RelayCommand]
    private Task Refresh()
    {
        return _refresh(this);
    }

    private static string Moment(long unixSeconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }
}
