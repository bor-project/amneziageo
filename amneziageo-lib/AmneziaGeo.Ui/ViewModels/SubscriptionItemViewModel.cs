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
    private string _offered = string.Empty;

    /// <summary>
    /// Почему сервер держит подписку выключенной; пусто, пока не держит.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOff))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(OffLine))]
    [NotifyPropertyChangedFor(nameof(OffText))]
    [NotifyPropertyChangedFor(nameof(Refreshable))]
    private string _fault = string.Empty;

    /// <summary>
    /// Дата, имя или удостоверяющий центр, о которых причина.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffLine))]
    [NotifyPropertyChangedFor(nameof(OffText))]
    private string _faultDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Refreshable))]
    private bool _busy;

    /// <summary>
    /// Заголовок строки: имя профиля с сервера, а без него - имя подписки.
    /// </summary>
    public string Caption => Title.Length > 0 ? Title : Name;

    /// <summary>
    /// Не прочиталась ли она в прошлый раз, пока сервер не держит её выключенной.
    /// </summary>
    public bool HasError => LastError.Length > 0 && !IsOff;

    /// <summary>
    /// Держит ли сервер подписку выключенной.
    /// </summary>
    public bool IsOff => Fault.Length > 0;

    /// <summary>
    /// Строка о выключенной подписке под именем открытой конфигурации.
    /// </summary>
    public string OffLine => IsOff ? Loc.Instance.Get("Main_SubscriptionOffLine", CertificateWords.Of(Fault, FaultDetail)) : string.Empty;

    /// <summary>
    /// Что сказать у знака «!» о выключенной подписке.
    /// </summary>
    public string OffText => IsOff ? Loc.Instance.Get("Main_SubscriptionOffText", CertificateWords.Of(Fault, FaultDetail)) : string.Empty;

    /// <summary>
    /// Можно ли перечитать подписку сейчас.
    /// </summary>
    public bool Refreshable => !Busy && !IsOff;

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
        OnPropertyChanged(nameof(OffLine));
        OnPropertyChanged(nameof(OffText));
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
