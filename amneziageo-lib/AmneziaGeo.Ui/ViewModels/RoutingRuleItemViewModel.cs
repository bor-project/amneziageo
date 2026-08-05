using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One entry of the open routing list: its rule token plus the collapsible preview of what a geo rule expands to.
/// </summary>
internal sealed partial class RoutingRuleItemViewModel : ViewModelBase
{
    /// <summary>
    /// Lines the preview box renders. A country runs to tens of thousands of entries and the box lays out every
    /// line it holds, which costs more than the entries themselves.
    /// </summary>
    public const int PreviewLines = 500;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingRuleItemViewModel(string token)
    {
        Token = token;
        CanExpand = token.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stored rule token ("geosite:github", "cidr:10.0.0.0/8").
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// True for geo categories - the only rules whose contents come from the geo databases.
    /// </summary>
    public bool CanExpand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _detailSummary = string.Empty;

    [ObservableProperty]
    private string _entriesText = string.Empty;

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
        EntriesText = string.Join(Environment.NewLine, entries.Count > PreviewLines ? entries.Take(PreviewLines) : entries);
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
        EntriesText = string.Empty;
        IsLoading = false;
        HasDetails = false;
    }

    /// <summary>
    /// Shows a failed fetch and leaves the item unloaded, so the next expand asks again.
    /// </summary>
    public void ShowError(string message)
    {
        DetailSummary = message;
        EntriesText = string.Empty;
        IsLoading = false;
    }
}
