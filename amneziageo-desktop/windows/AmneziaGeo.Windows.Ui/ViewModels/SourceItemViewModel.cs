using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A geo data source row on the routing page. Update / delete are dispatched as IPC commands via the
/// provided delegates.
/// </summary>
internal sealed partial class SourceItemViewModel : ViewModelBase
{
    private readonly Func<SourceItemViewModel, Task> _update;
    private readonly Func<SourceItemViewModel, Task> _remove;
    private readonly Func<SourceItemViewModel, Task> _edit;

    /// <summary>
    /// ctor
    /// </summary>
    public SourceItemViewModel(
        Func<SourceItemViewModel, Task> update,
        Func<SourceItemViewModel, Task> remove,
        Func<SourceItemViewModel, Task> edit)
    {
        _update = update;
        _remove = remove;
        _edit = edit;
    }

    /// <summary>
    /// The source kinds offered while editing.
    /// </summary>
    public IReadOnlyList<string> Kinds { get; } = ["geosite", "geoip"];

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
    /// The last download/parse failure for this source, or null when the last attempt succeeded.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progress;

    /// <summary>
    /// True when a newer remote file was found.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBadge))]
    private bool _updateAvailable;

    /// <summary>
    /// Whether to badge the row with "обновление доступно".
    /// </summary>
    public bool ShowUpdateBadge => UpdateAvailable && !Updating;

    /// <summary>
    /// A short label like "geosite · 1240 категорий · 2026-06-16 19:40", "geoip · не загружен", or
    /// "geoip · ошибка: …" when the last download/parse failed.
    /// </summary>
    public string Detail => Error is { Length: > 0 }
        ? Loc.Instance.Get("Source_DetailError", Kind, Error)
        : Updated is null
            ? Loc.Instance.Get("Source_DetailNotLoaded", Kind)
            : Loc.Instance.Get("Source_DetailLoaded", Kind, CategoryCount, Updated);

    /// <summary>
    /// True while a download percentage should be shown.
    /// </summary>
    public bool ShowProgress => Updating && Progress >= 0;

    /// <summary>
    /// The download percentage label shown before the spinning refresh icon.
    /// </summary>
    public string ProgressText => $"{Progress}%";

    /// <summary>
    /// True while the row shows its inline kind/url editor.
    /// </summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>
    /// The kind being edited in the inline editor.
    /// </summary>
    [ObservableProperty]
    private string _editKind = string.Empty;

    /// <summary>
    /// The url being edited in the inline editor.
    /// </summary>
    [ObservableProperty]
    private string _editUrl = string.Empty;

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

    [RelayCommand]
    private void BeginEdit()
    {
        EditKind = Kind;
        EditUrl = Url;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private async Task SaveEdit()
    {
        var url = EditUrl.Trim();
        if (url.Length == 0)
        {
            return;
        }

        EditUrl = url;
        await _edit(this);
        IsEditing = false;
    }
}
