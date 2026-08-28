using System.Globalization;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Строка подписки в каталоге: что она принесла, сколько трафика и когда её читали. Обновление и снятие
/// уходят командами агенту через переданные делегаты.
/// </summary>
internal sealed partial class SubscriptionItemViewModel : ViewModelBase
{
    private readonly Func<SubscriptionItemViewModel, Task> _refresh;
    private readonly Func<SubscriptionItemViewModel, bool, Task> _remove;

    /// <summary>
    /// ctor
    /// </summary>
    public SubscriptionItemViewModel(
        Func<SubscriptionItemViewModel, Task> refresh,
        Func<SubscriptionItemViewModel, bool, Task> remove)
    {
        _refresh = refresh;
        _remove = remove;
    }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private int _configs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private int _gone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private long _upload;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private long _download;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private long _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private long _expiresAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private long _checkedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _lastError = string.Empty;

    [ObservableProperty]
    private bool _busy;

    /// <summary>
    /// Ждёт ли строка ответа, снимать ли конфигурации вместе с подпиской.
    /// </summary>
    [ObservableProperty]
    private bool _removePending;

    /// <summary>
    /// Заголовок строки: имя профиля с сервера, а без него - имя подписки.
    /// </summary>
    public string Caption => Title.Length > 0 ? Title : Name;

    /// <summary>
    /// Не прочиталась ли она в прошлый раз.
    /// </summary>
    public bool HasError => LastError.Length > 0;

    /// <summary>
    /// Вторая строка: сколько конфигураций, трафик, срок и когда читали.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>
            {
                Loc.Instance.Get("Main_SubscriptionConfigs", Configs),
            };
            if (Gone > 0)
            {
                parts.Add(Loc.Instance.Get("Main_SubscriptionGone", Gone));
            }

            if (Upload + Download > 0 || Total > 0)
            {
                parts.Add(Total > 0
                    ? Loc.Instance.Get("Main_SubscriptionTraffic", Size(Upload + Download), Size(Total))
                    : Size(Upload + Download));
            }

            if (ExpiresAt > 0)
            {
                parts.Add(Loc.Instance.Get("Main_SubscriptionExpires", Moment(ExpiresAt)));
            }

            parts.Add(CheckedAt > 0
                ? Loc.Instance.Get("Main_SubscriptionChecked", Moment(CheckedAt))
                : Loc.Instance.Get("Main_SubscriptionNever"));

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Перечитывает подписи после смены языка.
    /// </summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Caption));
    }

    [RelayCommand]
    private Task Refresh()
    {
        return _refresh(this);
    }

    [RelayCommand]
    private void BeginRemove()
    {
        RemovePending = true;
    }

    [RelayCommand]
    private void CancelRemove()
    {
        RemovePending = false;
    }

    [RelayCommand]
    private Task RemoveKeepingConfigs()
    {
        RemovePending = false;
        return _remove(this, false);
    }

    [RelayCommand]
    private Task RemoveWithConfigs()
    {
        RemovePending = false;
        return _remove(this, true);
    }

    private static string Moment(long unixSeconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    private static string Size(long bytes)
    {
        string[] units = ["B", "K", "M", "G", "T"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#}{units[unit]}");
    }
}
