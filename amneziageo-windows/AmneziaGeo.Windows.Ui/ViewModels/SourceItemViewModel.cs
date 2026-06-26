using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A geo data source row on the routing page. Update / delete are dispatched as IPC commands to the
/// agent via the provided delegates (rather than parent-relative bindings, which do not resolve inside
/// the delete flyout's popup).
/// </summary>
internal sealed partial class SourceItemViewModel : ViewModelBase
{
    private readonly Func<SourceItemViewModel, Task> _update;
    private readonly Func<SourceItemViewModel, Task> _remove;

    /// <summary>
    /// ctor
    /// </summary>
    public SourceItemViewModel(Func<SourceItemViewModel, Task> update, Func<SourceItemViewModel, Task> remove)
    {
        _update = update;
        _remove = remove;
    }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _kind = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _updated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private int _categoryCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBadge))]
    private bool _updating;

    /// <summary>
    /// The last download/parse failure for this source (e.g. a wrong URL), or null when the last attempt
    /// succeeded. Folded into <see cref="Detail"/> so the row explains why a source has no categories.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progress;

    /// <summary>
    /// True when the last update-check found a newer remote file; the row badges it until re-downloaded.
    /// Hidden while a download is in flight (the spinner already shows the fresh state is coming).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBadge))]
    private bool _updateAvailable;

    /// <summary>Whether to badge the row with "обновление доступно".</summary>
    public bool ShowUpdateBadge => UpdateAvailable && !Updating;

    /// <summary>
    /// A short label like "geosite · 1240 категорий · 2026-06-16 19:40", "geoip · не загружен", or
    /// "geoip · ошибка: …" when the last download/parse failed.
    /// </summary>
    public string Detail => Error is { Length: > 0 }
        ? $"{Kind} · ошибка: {Error}"
        : Updated is null
            ? $"{Kind} · не загружен"
            : $"{Kind} · {CategoryCount} категорий · {Updated}";

    /// <summary>
    /// True while a download percentage should be shown (downloading); false once the file is in hand
    /// and the routing lists re-materialize, where the spinner runs indeterminate (Progress is -1).
    /// </summary>
    public bool ShowProgress => Updating && Progress >= 0;

    /// <summary>
    /// The download percentage label shown before the spinning refresh icon.
    /// </summary>
    public string ProgressText => $"{Progress}%";

    [RelayCommand]
    private Task Update()
    {
        return _update(this);
    }

    [RelayCommand]
    private Task Remove()
    {
        return _remove(this);
    }
}
