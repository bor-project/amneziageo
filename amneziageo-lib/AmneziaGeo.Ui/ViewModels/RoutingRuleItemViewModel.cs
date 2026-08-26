using AmneziaGeo.Decl;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One entry of the open routing list: its rule token, the servers it rides, and the collapsible preview of what
/// a geo rule expands to.
/// </summary>
internal sealed partial class RoutingRuleItemViewModel : ViewModelBase
{
    // True while the fields are being settled from the stored tail, so writing them back does not read as an edit.
    private bool _settling;

    private IReadOnlyList<string> _serverValues = [];
    private IReadOnlyList<string> _fallbackValues = [];

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingRuleItemViewModel(string token)
    {
        Token = token;
        HasPreview = UiPlatform.SupportsGeoPreview
            && (token.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase));
        IsTargetLocked = token.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("domain:", StringComparison.OrdinalIgnoreCase);
        Offer();
    }

    /// <summary>
    /// The stored rule token ("geosite:github", "cidr:10.0.0.0/8").
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// True for geo categories - the only rules whose contents come from the geo databases.
    /// </summary>
    public bool HasPreview { get; }

    /// <summary>
    /// True while the rule matches by name: those ride the default server until one resolver serves the machine.
    /// </summary>
    public bool IsTargetLocked { get; }

    /// <summary>
    /// True while the item opens: a geo category shows what it covers, a proxied rule the servers it rides.
    /// </summary>
    public bool CanExpand => HasPreview || ShowTargets;

    /// <summary>
    /// What the arrow promises.
    /// </summary>
    public string ExpandTooltip => HasPreview
        ? Loc.Instance.Get("Main_RuleEntriesTooltip")
        : Loc.Instance.Get("Main_RuleTargetsTooltip");

    /// <summary>
    /// Whether the rule shows the servers it rides: several servers are configured and it proxies.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExpand))]
    private bool _showTargets;

    /// <summary>
    /// Which server the rule rides.
    /// </summary>
    [ObservableProperty]
    private RuleTargetMode _serverMode = RuleTargetMode.Auto;

    /// <summary>
    /// Configuration the rule names; read while the mode names one.
    /// </summary>
    [ObservableProperty]
    private string _server = string.Empty;

    /// <summary>
    /// Where the rule goes while its server is down.
    /// </summary>
    [ObservableProperty]
    private RuleTargetMode _fallbackMode = RuleTargetMode.Auto;

    /// <summary>
    /// Configuration named as the second choice.
    /// </summary>
    [ObservableProperty]
    private string _fallback = string.Empty;

    /// <summary>
    /// What the server field offers.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _serverChoices = [];

    /// <summary>
    /// What the fallback field offers; it takes the two the server field cannot.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _fallbackChoices = [];

    /// <summary>
    /// Row picked in the server field.
    /// </summary>
    [ObservableProperty]
    private int _selectedServerIndex = -1;

    /// <summary>
    /// Row picked in the fallback field.
    /// </summary>
    [ObservableProperty]
    private int _selectedFallbackIndex = -1;

    /// <summary>
    /// Configurations the fields can name, priority top down.
    /// </summary>
    public IReadOnlyList<string> Servers
    {
        get => _servers;
        set
        {
            _servers = value;
            Offer();
        }
    }

    private IReadOnlyList<string> _servers = [];

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
    /// Collapse arrow, turned down while the item is open.
    /// </summary>
    public string Glyph => IsExpanded ? "▾" : "◂";

    /// <summary>
    /// True once the entries have been fetched into this item.
    /// </summary>
    public bool HasDetails { get; private set; }

    /// <summary>
    /// Settles both fields off the tail the stored token carries.
    /// </summary>
    public void ReadTail(string tail)
    {
        var fields = RuleFields.Split(tail);
        _settling = true;
        ServerMode = fields.ServerMode;
        Server = fields.Server;
        FallbackMode = fields.FallbackMode;
        Fallback = fields.Fallback;
        _settling = false;
        Offer();
    }

    /// <summary>
    /// Both fields as the tail of a stored token.
    /// </summary>
    public string WriteTail()
    {
        return RuleFields.Tail(ServerMode, Server, FallbackMode, Fallback);
    }

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
    /// Collapses the item and drops its fetched entries; the fields it carries stay.
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

    // A row of the server field was picked.
    partial void OnSelectedServerIndexChanged(int value)
    {
        if (_settling || value < 0 || value >= _serverValues.Count)
        {
            return;
        }

        var (mode, name) = RuleFields.Parse(_serverValues[value]);
        ServerMode = mode;
        Server = name;
    }

    // A row of the fallback field was picked.
    partial void OnSelectedFallbackIndexChanged(int value)
    {
        if (_settling || value < 0 || value >= _fallbackValues.Count)
        {
            return;
        }

        var (mode, name) = RuleFields.Parse(_fallbackValues[value]);
        FallbackMode = mode;
        Fallback = name;
    }

    // Rebuilds both menus against the configurations there are and points them at what the rule says.
    private void Offer()
    {
        var server = Build(ServerMode, Server, false);
        var fallback = Build(FallbackMode, Fallback, true);
        _serverValues = server.Values;
        _fallbackValues = fallback.Values;
        ServerChoices = server.Labels;
        FallbackChoices = fallback.Labels;

        _settling = true;
        SelectedServerIndex = Point(_serverValues, ServerMode, Server);
        SelectedFallbackIndex = Point(_fallbackValues, FallbackMode, Fallback);
        _settling = false;
    }

    private (IReadOnlyList<string> Labels, IReadOnlyList<string> Values) Build(RuleTargetMode mode, string named, bool fallback)
    {
        var labels = new List<string> { Loc.Instance.Get("Main_RuleTargetAuto"), Loc.Instance.Get("Main_RuleTargetBest") };
        var values = new List<string> { "auto", "best" };
        foreach (var name in Servers)
        {
            labels.Add(name);
            values.Add(name);
        }

        // A rule may name a configuration that is gone; it stays on the menu, so opening the list does not
        // retarget the rule behind the user's back.
        if (mode == RuleTargetMode.Server && named.Length > 0 && !values.Contains(named, StringComparer.Ordinal))
        {
            labels.Add(named);
            values.Add(named);
        }

        if (fallback)
        {
            labels.Add(Loc.Instance.Get("Main_RuleTargetDirect"));
            values.Add("direct");
            labels.Add(Loc.Instance.Get("Main_RuleTargetBlock"));
            values.Add("block");
        }

        return (labels, values);
    }

    private static int Point(IReadOnlyList<string> values, RuleTargetMode mode, string name)
    {
        var word = RuleFields.Word(mode, name);
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], word, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }
}
