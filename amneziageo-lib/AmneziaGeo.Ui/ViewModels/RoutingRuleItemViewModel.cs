using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One entry of the open routing list: its rule token plus the collapsible preview of what a geo rule expands to.
/// </summary>
internal partial class RoutingRuleItemViewModel : ViewModelBase
{
    private const string AppPrefix = "app:";

    // The program the token names for the device to find, empty for any other token.
    private readonly string _appName;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingRuleItemViewModel(string token)
    {
        Token = token;
        _appName = token.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase) && AppNames.IsName(token[AppPrefix.Length..])
            ? token[AppPrefix.Length..].Trim()
            : string.Empty;
        CanPreview = UiPlatform.SupportsGeoPreview
            && (token.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The stored rule token ("geosite:github", "cidr:10.0.0.0/8").
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// True for geo categories - the only rules whose contents come from the geo databases.
    /// </summary>
    public bool CanPreview { get; }

    /// <summary>
    /// True while the row opens under the arrow.
    /// </summary>
    public virtual bool CanExpand => CanPreview;

    /// <summary>
    /// True while the opened row carries the pickers of where the rule rides.
    /// </summary>
    public virtual bool HasRouteStrip => false;

    /// <summary>
    /// What the arrow promises.
    /// </summary>
    public virtual string ExpandTooltip => Loc.Instance.Get("Main_RuleEntriesTooltip");

    /// <summary>
    /// The program the token names for the device to find, empty for any other token.
    /// </summary>
    public string AppName => _appName;

    /// <summary>
    /// Kind and type of the entry, shown under its token; for a program named to be found, what the device found.
    /// </summary>
    public string Caption => _appName.Length == 0 ? RuleToken.Caption(Token) : AppCaption();

    /// <summary>
    /// Whether the device found no program the token names.
    /// </summary>
    public bool CaptionWarn => _appName.Length > 0 && Found is { Count: 0 };

    /// <summary>
    /// The programs the device found when it found more than one.
    /// </summary>
    public string? CaptionTip => _appName.Length > 0 && Found is { Count: > 1 } found ? string.Join(", ", found.Select(Shown)) : null;

    /// <summary>
    /// The app rule values the device found for the program the token names; null while it was never searched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    [NotifyPropertyChangedFor(nameof(CaptionWarn))]
    [NotifyPropertyChangedFor(nameof(CaptionTip))]
    private IReadOnlyList<string>? _found;

    // What the device found for the program, under the kind of the entry.
    private string AppCaption()
    {
        var kind = RuleToken.Kind(Token);
        return Found switch
        {
            null => Loc.Instance.Get("Main_RuleAppSearching", kind),
            { Count: 0 } => Loc.Instance.Get("Main_RuleAppMissing", kind),
            { Count: 1 } one => Loc.Instance.Get("Main_RuleAppFound", kind, Shown(one[0])),
            var many => Loc.Instance.Get("Main_RuleAppFound", kind, many.Count.ToString(CultureInfo.CurrentCulture)),
        };
    }

    // A found value the way people read it: the path, the folder or the package without its kind.
    private static string Shown(string value)
    {
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 ? value[(equals + 1)..] : value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _detailSummary = string.Empty;

    /// <summary>
    /// What the rule covers, one entry per row.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntries))]
    private IReadOnlyList<string> _entries = [];

    /// <summary>
    /// True while there are rows to show.
    /// </summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>
    /// Collapse arrow, turned down while the entries are shown.
    /// </summary>
    public string Glyph => IsExpanded ? "▾" : "◂";

    /// <summary>
    /// True once the entries have been fetched into this item.
    /// </summary>
    public bool HasDetails { get; private set; }

    /// <summary>
    /// Fills in the fetched entries and ends the loading state.
    /// </summary>
    public void ShowDetails(string summary, IReadOnlyList<string> entries)
    {
        DetailSummary = summary;
        Entries = entries;
        IsLoading = false;
        HasDetails = true;
    }

    /// <summary>
    /// Collapses the item and drops its fetched entries.
    /// </summary>
    public void Collapse()
    {
        IsExpanded = false;
        DetailSummary = string.Empty;
        Entries = [];
        IsLoading = false;
        HasDetails = false;
    }

    /// <summary>
    /// Shows a failed fetch and leaves the item unloaded, so the next expand asks again.
    /// </summary>
    public void ShowError(string message)
    {
        DetailSummary = message;
        Entries = [];
        IsLoading = false;
    }
}
