using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One entry of the open routing list: its rule token plus the collapsible preview of what a geo rule expands to.
/// </summary>
internal partial class RoutingRuleItemViewModel : ViewModelBase
{
    /// <summary>
    /// ctor
    /// </summary>
    public RoutingRuleItemViewModel(string token)
    {
        Token = token;
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
