using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Avalonia.Media.Imaging;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Editor for a shared routing list: name + rules (geo categories or manual domains / cidrs). Edits are held
/// in the buffer and persisted atomically through the agent on the header Save (#143).
/// </summary>
internal partial class RoutingListEditorViewModel : ViewModelBase, IEditScope
{
    private readonly IAgentConnection _connection;
    private readonly Action<long>? _onSaved;
    private long _id;

    // All geo category tokens from the last list-geo response.
    private readonly List<string> _allGeoTokens = [];

    // Dirty tracking suppressed during construction, initial load, and revert (#143).
    private bool _seeding = true;

    // Autosave: edits persist as they happen; a reconnect need surfaces via the standard banner.
    private bool _committing;
    private bool _commitPending;

    /// <summary>
    /// When set, edits persist through the agent as they happen (rules at once, name on blur).
    /// </summary>
    public bool AutoSave { get; set; }

    // Baseline captured on load / commit; the list is dirty when Name or any role bucket differs from it (a
    // new draft stays dirty until its first save).
    private string _baseName = string.Empty;
    private List<string> _baseProxy = [];
    private List<string> _baseDirect = [];
    private List<string> _baseBlock = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNameMissing))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleInput))]
    private string _ruleInput = string.Empty;

    // Set by the view: a wide editor drops the suggestions under the field, a narrow one lists them inline.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInlineSuggestions))]
    [NotifyPropertyChangedFor(nameof(ShowDropdownSuggestions))]
    private bool _isWideLayout;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // Required-field validation ("enter a name"), shown in red and cleared on any edit (#2/#3). Kept
    // separate from StatusMessage so import/success notices stay neutral, not red.
    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoading;

    // Add-entry method segment: address or application.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddressMethod))]
    [NotifyPropertyChangedFor(nameof(IsAppMethod))]
    [NotifyPropertyChangedFor(nameof(ShowRuleInput))]
    [NotifyPropertyChangedFor(nameof(RuleWatermark))]
    private string _addMethod = "address";

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingListEditorViewModel(IAgentConnection connection, Action<long>? onSaved = null)
        : this(connection, 0, string.Empty, onSaved)
    {
        IsNew = true;
    }

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingListEditorViewModel(IAgentConnection connection, long id, string name, Action<long>? onSaved = null)
    {
        _connection = connection;
        _onSaved = onSaved;
        _id = id;
        ProxyRules.CollectionChanged += OnRulesChanged;
        DirectRules.CollectionChanged += OnRulesChanged;
        BlockRules.CollectionChanged += OnRulesChanged;
        Name = name;
    }

    // Any role bucket changed: refresh suggestions/transfer, mark dirty, autosave (suppressed mid-sort or while seeding).
    private void OnRulesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Массовая заливка и сортировка перестраивают проекцию один раз, после себя: иначе каждая запись
        // пересобирает весь список, и вставка сотни правил встаёт в квадрат.
        if (_reordering || _seeding)
        {
            return;
        }

        if (ReferenceEquals(sender, Rules))
        {
            RebuildRuleItems();
        }

        _ = ApplySuggestionFilterAsync();
        MarkDirty();
        FireAutoSave();
    }

    /// <summary>
    /// True while creating a new list; cleared after the first save.
    /// </summary>
    [ObservableProperty]
    private bool _isNew;

    /// <summary>
    /// The persisted list id (0 until a new list is first saved).
    /// </summary>
    public long Id => _id;

    /// <summary>
    /// True when the name field is empty.
    /// </summary>
    public bool IsNameMissing => string.IsNullOrWhiteSpace(Name);

    // The ceiling belongs to sessions built without the relay - the agent knows whether this one is, so the
    // answer is asked for on every android and carries the ceiling in force.
    private static bool RouteBudgetApplies => OperatingSystem.IsAndroid();

    // The rule set the held answer belongs to; the same set is not asked about twice.
    private string _budgetSignature = string.Empty;

    // Discards a stale answer.
    private int _budgetToken;

    /// <summary>
    /// Routes the current rules turn into.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteBudgetExceeded))]
    private int _routeCount;

    /// <summary>
    /// Routes this device carries; 0 when it has no ceiling.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteBudgetExceeded))]
    private int _routeLimit;

    /// <summary>
    /// True when the rules turn into more routes than the device carries.
    /// </summary>
    public bool RouteBudgetExceeded => RouteLimit > 0 && RouteCount > RouteLimit;

    /// <summary>
    /// True when the list is wider than the tun takes and the widest direct ranges leave it: the session runs,
    /// the ranges held inside ride the tunnel instead of the physical path.
    /// </summary>
    [ObservableProperty]
    private bool _routeTrims;

    /// <summary>
    /// Direct ranges that keep the physical path once the list is cut to size.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteTrimNotice))]
    private int _routeKept;

    /// <summary>
    /// Direct ranges the list holds in total.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RouteTrimNotice))]
    private int _routeTotal;

    /// <summary>
    /// What the trim costs, in the terms the list is written in.
    /// </summary>
    public string RouteTrimNotice => Loc.Instance.Get("RoutingEditor_RoutesTrimmed", RouteKept, RouteTotal);

    /// <summary>
    /// Asks the agent what the current rules turn into and holds the answer.
    /// </summary>
    public async Task RefreshRouteBudgetAsync()
    {
        if (!RouteBudgetApplies)
        {
            return;
        }

        var rules = AllRoleTokens();
        var signature = (GlobalProxyActive ? "full\n" : "split\n") + string.Join('\n', rules);
        if (string.Equals(signature, _budgetSignature, StringComparison.Ordinal))
        {
            return;
        }

        var token = ++_budgetToken;
        if (rules.Count == 0)
        {
            _budgetSignature = signature;
            RouteCount = 0;
            RouteTrims = false;
            return;
        }

        var args = new List<string> { GlobalProxyActive ? "full" : "split" };
        args.AddRange(rules);
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpCountRoutes, args));
        if (token != _budgetToken || !ack.Ok)
        {
            return;
        }

        var answer = ParseBudget(ack.Message);
        _budgetSignature = signature;
        RouteLimit = answer.Limit;
        RouteCount = answer.Routes;
        RouteKept = answer.Kept;
        RouteTotal = answer.Total;
        RouteTrims = answer.Trims;
    }

    // Reads what the agent answers with; a malformed ack reads as no ceiling.
    private static (int Routes, int Limit, bool Trims, int Kept, int Total) ParseBudget(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var routes = root.TryGetProperty("routes", out var count) && count.TryGetInt32(out var value) ? value : 0;
            var limit = root.TryGetProperty("limit", out var cap) && cap.TryGetInt32(out var ceiling) ? ceiling : 0;
            var trims = root.TryGetProperty("trims", out var cut) && cut.TryGetInt32(out var cutting) && cutting != 0;
            var kept = root.TryGetProperty("kept", out var held) && held.TryGetInt32(out var holding) ? holding : 0;
            var total = root.TryGetProperty("total", out var all) && all.TryGetInt32(out var whole) ? whole : 0;
            return (routes, limit, trims, kept, total);
        }
        catch (JsonException)
        {
            return (0, 0, false, 0, 0);
        }
    }

    /// <summary>
    /// The Proxy bucket: tunneled while the global proxy is off.
    /// </summary>
    public ObservableCollection<string> ProxyRules { get; } = [];

    /// <summary>
    /// The Direct bucket: bypasses the tunnel in either mode, overriding a proxy match.
    /// </summary>
    public ObservableCollection<string> DirectRules { get; } = [];

    /// <summary>
    /// The Block bucket: always blocked.
    /// </summary>
    public ObservableCollection<string> BlockRules { get; } = [];

    // The bucket currently shown/edited by the role segment.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rules))]
    [NotifyPropertyChangedFor(nameof(IsProxyRole))]
    [NotifyPropertyChangedFor(nameof(IsDirectRole))]
    [NotifyPropertyChangedFor(nameof(IsBlockRole))]
    [NotifyPropertyChangedFor(nameof(RoleHint))]
    [NotifyPropertyChangedFor(nameof(CanAddApps))]
    private string _selectedRole = "proxy";

    // Mirrors the list's global-proxy flag, kept in sync by RoutingViewModel.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseProxyBucket))]
    private bool _globalProxyActive;

    // Mirrors the list's all-UDP flag, kept in sync by RoutingViewModel.
    [ObservableProperty]
    private bool _allUdpActive;

    partial void OnGlobalProxyActiveChanged(bool value)
    {
        if (value && IsProxyRole)
        {
            SelectedRole = "direct";
        }

        RefreshTransfer();
        _ = RefreshRouteBudgetAsync();
    }

    partial void OnAllUdpActiveChanged(bool value) => RefreshTransfer();

    /// <summary>
    /// The active bucket's rule tokens (geosite:openai etc), selected by <see cref="SelectedRole"/>.
    /// </summary>
    public ObservableCollection<string> Rules => SelectedRole switch
    {
        "direct" => DirectRules,
        "block" => BlockRules,
        _ => ProxyRules,
    };

    /// <summary>
    /// The shown bucket as list items, each carrying its own collapse state and geo entry preview.
    /// </summary>
    public ObservableCollection<RoutingRuleItemViewModel> RuleItems { get; } = [];

    // Rules left expanded, kept across a projection rebuild.
    private readonly HashSet<string> _expandedRules = new(StringComparer.Ordinal);

    // Fetched geo entries per rule token, dropped when the screen is left.
    private readonly Dictionary<string, GeoEntries> _detailCache = new(StringComparer.Ordinal);

    /// <summary>
    /// What a geo rule expands to: everything it covers, and the page the agent handed over.
    /// </summary>
    private sealed record GeoEntries(int Total, IReadOnlyList<string> Entries);

    // Bumped on every drop, so a fetch in flight cannot land into the cleared state.
    private int _detailsGeneration;

    /// <summary>
    /// Drops the fetched entries and collapses every rule.
    /// </summary>
    public void ClearRuleDetails()
    {
        _detailsGeneration++;
        _expandedRules.Clear();
        _detailCache.Clear();
        foreach (var item in RuleItems)
        {
            item.Collapse();
        }
    }

    /// <summary>
    /// The row one rule is shown in. The mode's row carries the servers the rule may ride.
    /// </summary>
    protected virtual RoutingRuleItemViewModel NewRuleRow(string token)
    {
        return new RoutingRuleItemViewModel(token);
    }

    // Rebuilds the shown bucket's projection, carrying expansion state and already-fetched entries over.
    protected void RebuildRuleItems()
    {
        RuleItems.Clear();
        foreach (var token in Rules)
        {
            var item = NewRuleRow(token);
            if (item.CanExpand && _expandedRules.Contains(token))
            {
                item.IsExpanded = true;
                if (_detailCache.TryGetValue(token, out var cached))
                {
                    item.ShowDetails(Summarize(cached.Total, cached.Entries.Count), cached.Entries);
                }
                else
                {
                    _ = LoadRuleDetailsAsync(item);
                }
            }

            RuleItems.Add(item);
        }

        RefreshCounts();
    }

    /// <summary>
    /// Expands or collapses a geo rule's entries, fetching them from the agent on the first expand.
    /// </summary>
    [RelayCommand]
    private async Task ToggleRuleDetailsAsync(RoutingRuleItemViewModel item)
    {
        item.IsExpanded = !item.IsExpanded;
        if (!item.IsExpanded)
        {
            _expandedRules.Remove(item.Token);
            item.Collapse();
            return;
        }

        _expandedRules.Add(item.Token);
        if (!item.HasDetails && !item.IsLoading)
        {
            await LoadRuleDetailsAsync(item);
        }
    }

    // Fetches a rule's entries through the agent and memoizes them for the session.
    private async Task LoadRuleDetailsAsync(RoutingRuleItemViewModel item)
    {
        if (!item.CanPreview)
        {
            return;
        }

        if (_detailCache.TryGetValue(item.Token, out var cached))
        {
            item.ShowDetails(Summarize(cached.Total, cached.Entries.Count), cached.Entries);
            return;
        }

        item.IsLoading = true;
        var generation = _detailsGeneration;
        // Ask for the whole category: the row list virtualizes, so it costs what is on screen.
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetGeoEntries, [item.Token, "0"]));
        if (generation != _detailsGeneration)
        {
            return;
        }

        if (!ack.Ok)
        {
            item.ShowError(ack.Message);
            return;
        }

        var preview = ParseEntries(ack.Message);
        _detailCache[item.Token] = preview;
        item.ShowDetails(Summarize(preview.Total, preview.Entries.Count), preview.Entries);
    }

    // Reads the preview the agent hands over: what the rule covers in full, and the page of it that came along. An
    // older agent answers with the page alone, so a bare array counts as the whole set. A malformed ack reads as empty.
    private static GeoEntries ParseEntries(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var flat = Read(doc.RootElement);
                return new GeoEntries(flat.Count, flat);
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("entries", out var array))
            {
                return new GeoEntries(0, []);
            }

            var entries = Read(array);
            var total = doc.RootElement.TryGetProperty("total", out var count) && count.TryGetInt32(out var value)
                ? value
                : entries.Count;
            return new GeoEntries(total, entries);
        }
        catch (JsonException)
        {
            return new GeoEntries(0, []);
        }
    }

    private static IReadOnlyList<string> Read(JsonElement array)
    {
        return array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : [];
    }

    // Localized line above the entries; a rule bigger than the page says how much of it is shown.
    private static string Summarize(int total, int shown) => total switch
    {
        0 => Loc.Instance.Get("RoutingEditor_RuleNoEntries"),
        _ when shown < total => Loc.Instance.Get("RoutingEditor_RuleEntriesShown", shown, total),
        _ => Loc.Instance.Get("RoutingEditor_RuleEntriesCount", total),
    };

    public bool IsProxyRole => SelectedRole == "proxy";

    public bool IsDirectRole => SelectedRole == "direct";

    public bool IsBlockRole => SelectedRole == "block";

    /// <summary>
    /// True while the shown bucket holds entries.
    /// </summary>
    public bool HasRules => Rules.Count > 0;

    /// <summary>
    /// Proxy tab caption.
    /// </summary>
    public string ProxyTabText => Loc.Instance.Get("Main_RoleProxy");

    /// <summary>
    /// Direct tab caption.
    /// </summary>
    public string DirectTabText => Loc.Instance.Get("Main_RoleDirect");

    /// <summary>
    /// Block tab caption.
    /// </summary>
    public string BlockTabText => Loc.Instance.Get("Main_RoleBlock");

    // Re-reads the counters and the labels naming the shown bucket.
    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(ProxyTabText));
        OnPropertyChanged(nameof(DirectTabText));
        OnPropertyChanged(nameof(BlockTabText));
        OnPropertyChanged(nameof(HasRules));
    }

    /// <summary>
    /// True while the Proxy bucket is pickable: the full tunnel carries everything by itself.
    /// </summary>
    public bool CanUseProxyBucket => !GlobalProxyActive;

    /// <summary>
    /// Localized help line for the active role.
    /// </summary>
    public string RoleHint => SelectedRole switch
    {
        "direct" => Loc.Instance.Get("Main_RoleDirectHint"),
        "block" => Loc.Instance.Get("Main_RoleBlockHint"),
        _ => Loc.Instance.Get("Main_RoleProxyHint"),
    };

    // After the active bucket swaps, re-project it and refresh the suggestion filter for the newly shown bucket.
    partial void OnSelectedRoleChanged(string value)
    {
        if (!CanAddApps && !IsAddressMethod)
        {
            AddMethod = "address";
        }

        RebuildRuleItems();
        _ = ApplySuggestionFilterAsync();
    }

    // Total entries across all buckets.
    private int TotalRules => ProxyRules.Count + DirectRules.Count + BlockRules.Count;

    // The bucket for a role token.
    private ObservableCollection<string> BucketFor(string role) => role switch
    {
        "direct" => DirectRules,
        "block" => BlockRules,
        _ => ProxyRules,
    };

    // Splits a "role|token" into (role, token); a bare token is proxy.
    private static (string Role, string Token) SplitRoleToken(string text)
    {
        var bar = text.IndexOf('|');
        if (bar > 0)
        {
            var role = text[..bar].ToLowerInvariant();
            // Legacy "exclude" folds into Direct (Exclude bucket removed).
            if (role == "exclude")
            {
                return ("direct", text[(bar + 1)..]);
            }

            if (role is "proxy" or "direct" or "block")
            {
                return (role, text[(bar + 1)..]);
            }
        }

        return ("proxy", text);
    }

    /// <summary>
    /// Geo category suggestions for the rule input, fetched from the agent.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _geoSuggestions = [];

    /// <summary>
    /// True when the add-entry segment targets address entries (geo / domain / cidr).
    /// </summary>
    public bool IsAddressMethod => AddMethod == "address";

    /// <summary>
    /// True when the add-entry segment targets per-application entries.
    /// </summary>
    public bool IsAppMethod => AddMethod == "app";

    /// <summary>
    /// True while the typed add row stands: Android picks its packages through the system sheet instead.
    /// </summary>
    public bool ShowRuleInput => !IsAppMethod || !IsAppSourceAndroid;

    /// <summary>
    /// True when the per-application entry method is offered (Windows path matching or the Android package picker).
    /// </summary>
    public bool IsAppMethodAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsAndroid();

    /// <summary>
    /// True while the application method is pickable: only the Proxy bucket runs it.
    /// </summary>
    public bool CanAddApps => IsProxyRole;

    /// <summary>
    /// App tab caption, empty where the platform runs no app rules.
    /// </summary>
    public string AppTabText => IsAppMethodAvailable ? Loc.Instance.Get("Main_AddByAppTab") : string.Empty;

    /// <summary>
    /// Watermark of the add row, reflecting the selected method.
    /// </summary>
    public string RuleWatermark => IsAppMethod
        ? Loc.Instance.Get("Main_AddAppWatermark")
        : Loc.Instance.Get("Main_AddEntryWatermark");

    /// <summary>
    /// True when applications are named by path: the file and folder pickers stand there.
    /// </summary>
    public bool IsAppSourceWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// True when the app source is the Android installed-app picker.
    /// </summary>
    public bool IsAppSourceAndroid => OperatingSystem.IsAndroid();

    /// <summary>
    /// Best-match suggestions for the current input, shown under the field.
    /// </summary>
    public ObservableCollection<RoutingSuggestionViewModel> MatchedSuggestions { get; } = [];

    /// <summary>
    /// The head of the match list, shown inline where a dropdown does not fit.
    /// </summary>
    public ObservableCollection<RoutingSuggestionViewModel> TopSuggestions { get; } = [];

    /// <summary>
    /// True when the suggestion list has entries to show.
    /// </summary>
    public bool HasMatchedSuggestions => MatchedSuggestions.Count > 0;

    /// <summary>
    /// True while the suggestions stand as rows under the field.
    /// </summary>
    public bool ShowInlineSuggestions => HasMatchedSuggestions && !IsWideLayout;

    /// <summary>
    /// True while the suggestions come as a dropdown over the entries.
    /// </summary>
    public bool ShowDropdownSuggestions => HasMatchedSuggestions && IsWideLayout;

    /// <summary>
    /// True while the add row holds text.
    /// </summary>
    public bool HasRuleInput => RuleInput.Length > 0;

    // How many suggestions the dropdown and the inline list hold.
    private const int SuggestionLimit = 8;

    private const int InlineSuggestionLimit = 3;

    // Rebuilds the pick list: what the input adds as it stands, then the geo categories containing it.
    private void UpdateMatchedSuggestions()
    {
        MatchedSuggestions.Clear();
        TopSuggestions.Clear();
        var query = RuleInput.Trim();
        if (query.Length > 0 && IsAddressMethod)
        {
            var typed = Normalize(query);
            if (typed.Length > 0 && !Rules.Contains(typed))
            {
                MatchedSuggestions.Add(new RoutingSuggestionViewModel(typed));
            }

            foreach (var token in GeoSuggestions
                .Where(t => t.Contains(query, StringComparison.OrdinalIgnoreCase) && t != typed)
                .Take(SuggestionLimit))
            {
                MatchedSuggestions.Add(new RoutingSuggestionViewModel(token));
            }
        }
        else if (query.Length > 0 && IsAppMethod)
        {
            foreach (var match in MatchApps(query))
            {
                MatchedSuggestions.Add(match);
            }
        }

        foreach (var item in MatchedSuggestions.Take(InlineSuggestionLimit))
        {
            TopSuggestions.Add(item);
        }

        OnPropertyChanged(nameof(HasMatchedSuggestions));
        OnPropertyChanged(nameof(ShowInlineSuggestions));
        OnPropertyChanged(nameof(ShowDropdownSuggestions));
    }

    // Running and installed applications answer one query: each source keeps its own group, a name met twice
    // stands once, what the list already holds drops out, and the head of the result is offered.
    private IReadOnlyList<RoutingSuggestionViewModel> MatchApps(string query)
    {
        var held = new HashSet<string>(Rules.Select(AppPathToken.NormalizeAppRule), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<RoutingSuggestionViewModel>();
        foreach (var group in _appGroups)
        {
            foreach (var candidate in group.Where(c => Names(c, query)))
            {
                var token = AppPathToken.NormalizeAppRule(candidate.Token);
                if (held.Contains(token) || !seen.Add(token))
                {
                    continue;
                }

                matches.Add(new RoutingSuggestionViewModel(token, candidate.Display));
                if (matches.Count == SuggestionLimit)
                {
                    return matches;
                }
            }
        }

        return matches;
    }

    // Matches the name and the path alike: both get typed.
    private static bool Names(AppCandidate candidate, string query) =>
        candidate.Display.Contains(query, StringComparison.OrdinalIgnoreCase)
        || candidate.Token.Contains(query, StringComparison.OrdinalIgnoreCase);

    partial void OnRuleInputChanged(string value) => UpdateMatchedSuggestions();

    partial void OnGeoSuggestionsChanged(IReadOnlyList<string> value) => UpdateMatchedSuggestions();

    /// <summary>
    /// Re-raises the localized computed labels after a language change.
    /// </summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(RuleWatermark));
        OnPropertyChanged(nameof(AppTabText));
        OnPropertyChanged(nameof(RoleHint));
        RefreshCounts();
        UpdateMatchedSuggestions();

        // Entry counts read from the cache carry a localized line; re-project to render it in the new language.
        RebuildRuleItems();

        // App suggestions bake a localized kind prefix at load; rebuild them for the new language.
        if (_appGroups.Count > 0)
        {
            _appGroups.Clear();
            _ = LoadAppsAsync();
        }
    }

    /// <summary>
    /// Fetches geo category suggestions and the current rules for an existing list.
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await RefreshSuggestionsAsync();

            if (_id != 0)
            {
                var detail = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetRoutingList, [_id.ToString(CultureInfo.InvariantCulture)]));
                if (detail.Ok)
                {
                    // Replaces the buckets: a second load of the same editor would otherwise list every rule twice.
                    ProxyRules.Clear();
                    DirectRules.Clear();
                    BlockRules.Clear();
                    foreach (var token in detail.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var (role, plain) = SplitRoleToken(token);
                        BucketFor(role).Add(plain);
                    }
                }

                await ApplySuggestionFilterAsync();
            }

            // Seeding done: snapshot the loaded state as the clean baseline; edits from here mark the item dirty.
            _seeding = false;
            RebuildRuleItems();
            CaptureBaseline();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Re-fetches geo category suggestions from the agent, replacing the current set.
    /// </summary>
    public async Task RefreshSuggestionsAsync()
    {
        var response = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListGeo, []));
        if (!response.Ok)
        {
            return;
        }

        // Cache the full set; derive the visible suggestions.
        var tokens = response.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _allGeoTokens.Clear();
        _allGeoTokens.AddRange(tokens);
        await ApplySuggestionFilterAsync();
    }

    // Discards a stale filter result.
    private int _suggestionFilterToken;

    /// <summary>
    /// Rebuilds GeoSuggestions from cached tokens, dropping rules already in the list.
    /// </summary>
    private async Task ApplySuggestionFilterAsync()
    {
        var token = ++_suggestionFilterToken;
        var selected = new HashSet<string>(Rules, StringComparer.Ordinal);
        var pool = _allGeoTokens.ToArray();

        var filtered = await Task.Run(() => pool
            .Where(t => !selected.Contains(t))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList());

        if (token != _suggestionFilterToken)
        {
            return;
        }

        GeoSuggestions = filtered;
    }

    /// <summary>
    /// Saves the list (insert or update) through the agent.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        var trimmed = Name.Trim();
        if (trimmed.Length == 0)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_EnterRuleName");
            return false;
        }

        // A list this device cannot carry is refused on a fresh count, not on whatever the last edit left behind.
        await RefreshRouteBudgetAsync();
        if (RouteBudgetExceeded)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_TooManyRoutes", RouteCount, RouteLimit);
            return false;
        }

        IsBusy = true;
        try
        {
            var args = new List<string> { _id.ToString(CultureInfo.InvariantCulture), trimmed };
            args.AddRange(ProxyRules.Select(r => $"proxy|{r}"));
            args.AddRange(DirectRules.Select(r => $"direct|{r}"));
            args.AddRange(BlockRules.Select(r => $"block|{r}"));
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSaveRoutingList, args));
            if (ack.Ok && long.TryParse(ack.Message, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resultId))
            {
                _id = resultId;
            }

            // Only a failure reason stays inline; a reconnect need shows via the standard banner (RestartRequired).
            StatusMessage = ack.Ok ? string.Empty : ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Deletes the list through the agent.
    /// </summary>
    public async Task<bool> DeleteAsync()
    {
        if (_id == 0)
        {
            return true;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveRoutingList, [_id.ToString(CultureInfo.InvariantCulture)]));
            StatusMessage = ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <inheritdoc />
    public bool IsDirty { get; private set; }

    /// <inheritdoc />
    public event EventHandler? DirtyChanged;

    // Re-evaluate dirtiness against the baseline (a new draft stays dirty until first saved). Skipped while
    // seeding / reverting so those bulk field writes do not flip the flag mid-way (#143).
    private void MarkDirty()
    {
        if (_seeding)
        {
            return;
        }

        // Any edit clears a stale required-field validation line (#3).
        ValidationMessage = string.Empty;

        var dirty = IsNew
            || HasPendingEdits
            || !string.Equals(Name, _baseName, StringComparison.Ordinal)
            || !ProxyRules.SequenceEqual(_baseProxy, StringComparer.Ordinal)
            || !DirectRules.SequenceEqual(_baseDirect, StringComparer.Ordinal)
            || !BlockRules.SequenceEqual(_baseBlock, StringComparer.Ordinal);
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }

        RefreshTransfer();
        _ = RefreshRouteBudgetAsync();
    }

    /// <summary>
    /// Edits an heir holds beyond the name and the buckets.
    /// </summary>
    protected virtual bool HasPendingEdits => false;

    /// <summary>
    /// Re-reads the edits against the baseline.
    /// </summary>
    protected void RefreshDirty() => MarkDirty();

    /// <inheritdoc />
    public bool CanCommit()
    {
        if (Name.Trim().Length == 0)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_EnterRuleName");
            return false;
        }

        if (RouteBudgetExceeded)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_TooManyRoutes", RouteCount, RouteLimit);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public virtual void CaptureBaseline()
    {
        _baseName = Name;
        _baseProxy = ProxyRules.ToList();
        _baseDirect = DirectRules.ToList();
        _baseBlock = BlockRules.ToList();
        MarkDirty();
    }

    /// <inheritdoc />
    public virtual void Revert()
    {
        _seeding = true;
        try
        {
            Name = _baseName;
            RestoreBucket(ProxyRules, _baseProxy);
            RestoreBucket(DirectRules, _baseDirect);
            RestoreBucket(BlockRules, _baseBlock);
            RuleInput = string.Empty;
            StatusMessage = string.Empty;
            ValidationMessage = string.Empty;
        }
        finally
        {
            _seeding = false;
        }

        RebuildRuleItems();
        _ = ApplySuggestionFilterAsync();
        MarkDirty();
    }

    private static void RestoreBucket(ObservableCollection<string> bucket, IReadOnlyList<string> baseline)
    {
        bucket.Clear();
        foreach (var rule in baseline)
        {
            bucket.Add(rule);
        }
    }

    /// <summary>
    /// Persists the list through the agent (#143 header Save). On a new list's first save it adopts the real
    /// id, clears IsNew, and notifies the host so its per-routing settings editor is built. Returns success.
    /// </summary>
    public virtual async Task<bool> CommitAsync()
    {
        var wasNew = _id == 0;
        if (!await SaveAsync())
        {
            return false;
        }

        if (wasNew && _id != 0)
        {
            IsNew = false;
            _onSaved?.Invoke(_id);
        }

        return true;
    }

    // Fire-and-forget autosave when a rule is added / removed (skipped while seeding the initial load).
    private void FireAutoSave()
    {
        if (AutoSave && !_seeding)
        {
            _ = AutoSaveAsync();
        }
    }

    /// <summary>
    /// Serialized autosave: persists name + rules through the agent, re-running when an edit lands mid-commit. A
    /// draft with no name stays unsaved silently - there is nothing to persist yet.
    /// </summary>
    public async Task AutoSaveAsync()
    {
        if (_seeding || !AutoSave)
        {
            return;
        }

        await PersistAsync();
    }

    // Persists the list, queueing an edit behind the commit in flight.
    private async Task PersistAsync()
    {
        if (Name.Trim().Length == 0)
        {
            return;
        }

        if (_committing)
        {
            _commitPending = true;
            return;
        }

        _committing = true;
        try
        {
            do
            {
                _commitPending = false;
                if (!IsDirty)
                {
                    break;
                }

                if (await CommitAsync() && !_commitPending)
                {
                    CaptureBaseline();
                }
            }
            while (_commitPending);
        }
        finally
        {
            _committing = false;
        }
    }

    // Switches the active role bucket shown/edited by the segment.
    [RelayCommand]
    private void SelectRole(string role)
    {
        SelectedRole = role;
    }

    // Switches the add-entry method segment between address and per-application entries.
    [RelayCommand]
    private void SelectAddMethod(string method)
    {
        AddMethod = method;
    }

    // Both censuses cost more than a list open is worth, so they are taken when the application tab is first
    // opened. Android picks apps through its own sheet.
    partial void OnAddMethodChanged(string value)
    {
        RuleInput = string.Empty;
        UpdateMatchedSuggestions();
        if (value == "app" && IsAppSourceWindows && _appGroups.Count == 0)
        {
            _ = LoadAppsAsync();
        }
    }

    /// <summary>
    /// Fetches the machine's own subnets and the private networks of the configurations, and adds them to the selected bucket.
    /// </summary>
    [RelayCommand]
    private async Task AddLocalSubnetsAsync()
    {
        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListLocalSubnets, []));
            if (!ack.Ok)
            {
                StatusMessage = ack.Message;
                return;
            }

            var subnets = new List<string>(SubnetLines(ack.Message));
            // An agent that predates the command answers with a refusal; its own subnets still stand.
            var tunnels = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListTunnelSubnets, []));
            if (tunnels.Ok)
            {
                subnets.AddRange(SubnetLines(tunnels.Message));
            }

            var bucket = Rules;
            var added = 0;
            foreach (var subnet in subnets)
            {
                var rule = Normalize(subnet);
                if (rule.Length > 0 && !bucket.Contains(rule))
                {
                    bucket.Add(rule);
                    added++;
                }
            }

            StatusMessage = added > 0
                ? Loc.Instance.Get("RoutingSettings_LocalSubnetsAdded", added)
                : subnets.Count == 0
                    ? Loc.Instance.Get("RoutingSettings_NoActiveLocalSubnets")
                    : Loc.Instance.Get("RoutingSettings_AllLocalSubnetsPresent");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Splits a newline-separated subnet payload.
    private static string[] SubnetLines(string payload) =>
        payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [RelayCommand]
    private void AddRule()
    {
        var text = RuleInput.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (IsAppMethod)
        {
            AddAppToken(AppTokenOf(text));
            RuleInput = string.Empty;
            return;
        }

        var rule = Normalize(text);
        if (!Rules.Contains(rule))
        {
            Rules.Add(rule);
        }

        RuleInput = string.Empty;
    }

    // What a typed or picked application entry names: a folder if it stands on disk, a program file otherwise.
    private static string AppTokenOf(string text)
    {
        if (text.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var path = Environment.ExpandEnvironmentVariables(text);
        return System.IO.Directory.Exists(path) ? "app:dir=" + text : "app:path=" + text;
    }

    /// <summary>
    /// Adds a suggestion picked from the inline match list to the active bucket and clears the input.
    /// </summary>
    [RelayCommand]
    private void PickSuggestion(string token)
    {
        if (token.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            AddAppToken(token);
            RuleInput = string.Empty;
            return;
        }

        var rule = Normalize(token);
        if (rule.Length > 0 && !Rules.Contains(rule))
        {
            Rules.Add(rule);
        }

        RuleInput = string.Empty;
    }

    /// <summary>
    /// Empties the add row.
    /// </summary>
    [RelayCommand]
    private void ClearRuleInput()
    {
        RuleInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveRule(string rule)
    {
        _expandedRules.Remove(rule);
        Rules.Remove(rule);
    }

    /// <summary>
    /// Clears all entries of this list at once.
    /// </summary>
    [RelayCommand]
    private void ClearRules()
    {
        foreach (var rule in Rules)
        {
            _expandedRules.Remove(rule);
        }

        Rules.Clear();
    }

    // Direction of the next sort; the first click sorts ascending.
    private bool _sortDescending = true;

    // Suppresses per-item side effects while a sort reorders the collection in place.
    private bool _reordering;

    /// <summary>
    /// Reorders entries by name, flipping direction on each invocation. A saved list stores the order at once;
    /// a list with edits under way carries it into their save.
    /// </summary>
    [RelayCommand]
    private void SortRules()
    {
        var wasSaved = !IsDirty;
        _sortDescending = !_sortDescending;
        var ordered = (_sortDescending
                ? Rules.OrderByDescending(rule => rule, StringComparer.OrdinalIgnoreCase)
                : Rules.OrderBy(rule => rule, StringComparer.OrdinalIgnoreCase))
            .ToList();

        _reordering = true;
        try
        {
            for (var target = 0; target < ordered.Count; target++)
            {
                var current = Rules.IndexOf(ordered[target]);
                if (current != target)
                {
                    Rules.Move(current, target);
                }
            }
        }
        finally
        {
            _reordering = false;
        }

        RebuildRuleItems();
        MarkDirty();
        if (wasSaved)
        {
            _ = PersistAsync();
            return;
        }

        FireAutoSave();
    }

    // Per-app tunneling: the pools the input searches + the token-add path.

    // What the input matches against: running applications and services first, installed ones after.
    private readonly List<IReadOnlyList<AppCandidate>> _appGroups = [];

    // Both censuses cost a round-trip, so they are taken when the application tab is first opened.
    private async Task LoadAppsAsync()
    {
        var running = await LoadRunningAsync();
        var installed = await Task.Run(InstalledApps.List);
        _appGroups.Clear();
        _appGroups.Add(running);
        _appGroups.Add(installed);
        UpdateMatchedSuggestions();
    }

    private async Task<IReadOnlyList<AppCandidate>> LoadRunningAsync()
    {
        var response = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListProcesses, []));
        if (!response.Ok)
        {
            return [];
        }

        var candidates = new List<AppCandidate>();
        foreach (var line in response.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length < 3)
            {
                continue;
            }

            var kind = fields[0];
            var label = fields[1];
            var value = fields[2];
            string token;
            string display;
            if (string.Equals(kind, "service", StringComparison.Ordinal))
            {
                token = $"app:svc={value}";
                display = Loc.Instance.Get("RoutingEditor_AppKindService", label);
            }
            else
            {
                // Default an app to its containing folder, hoisted above a versioned subfolder so the rule
                // survives the app's auto-update into a new version folder (#204).
                var dir = System.IO.Path.GetDirectoryName(value);
                token = !string.IsNullOrEmpty(dir)
                    ? $"app:dir={AppPathToken.StripVersionedLeaf(dir)}"
                    : $"app:path={value}";
                display = Loc.Instance.Get("RoutingEditor_AppKindApplication", label);
            }

            candidates.Add(new AppCandidate(display, token));
        }

        return candidates;
    }

    /// <summary>
    /// Adds an app matcher token (app:path=/dir=/svc=) after a safety check.
    /// </summary>
    public void AddAppToken(string token)
    {
        if (!IsAppMatcherSafe(token, out var reason))
        {
            StatusMessage = reason;
            return;
        }

        // Store a version-independent package match for Store apps, else a portable %ENV% path, so the rule
        // survives the app auto-updating and export to another machine or user.
        token = AmneziaGeo.Ipc.AppPathToken.NormalizeAppRule(token);
        if (!Rules.Contains(token))
        {
            Rules.Add(token);
        }
    }

    /// <summary>
    /// Opens the platform app picker (Android) and applies the chosen packages to the Proxy bucket (include).
    /// </summary>
    [RelayCommand]
    private void PickApps()
    {
        if (!AppSplitBridge.IsAvailable)
        {
            return;
        }

        // App rules are include-only: they route the picked apps through the tunnel, so they live in Proxy.
        SelectedRole = "proxy";
        AppSplitBridge.Present(SelectedAppPackages(), ApplyPickedApps);
    }

    // The package names already in the Proxy bucket as app:pkg rules, for pre-checking the picker.
    private IReadOnlyList<string> SelectedAppPackages()
    {
        const string prefix = "app:pkg=";
        return [.. ProxyRules
            .Where(r => r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(r => r[prefix.Length..])];
    }

    // Replaces the Proxy bucket's app:pkg rules with the picked package set.
    private void ApplyPickedApps(IReadOnlyCollection<string> packages)
    {
        const string prefix = "app:pkg=";
        for (var i = ProxyRules.Count - 1; i >= 0; i--)
        {
            if (ProxyRules[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _expandedRules.Remove(ProxyRules[i]);
                ProxyRules.RemoveAt(i);
            }
        }

        foreach (var package in packages)
        {
            var token = prefix + package;
            if (!ProxyRules.Contains(token))
            {
                ProxyRules.Add(token);
            }
        }
    }

    // Rejects matchers that would tunnel far more than one app.
    private static bool IsAppMatcherSafe(string token, out string reason)
    {
        reason = string.Empty;

        // A rule on this application sends the agent's own downloads, the DNS proxy upstream and the websocket
        // carrier into the tunnel they run.
        if (OwnAppRule.Names(token))
        {
            reason = Loc.Instance.Get("RoutingEditor_OwnAppRule");
            return false;
        }

        if (token.StartsWith("app:svc=", StringComparison.OrdinalIgnoreCase))
        {
            return true; // a single named service
        }

        var eq = token.IndexOf('=');
        var value = (eq >= 0 ? token[(eq + 1)..] : string.Empty).Trim();
        var norm = value.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        if (norm.Length == 0)
        {
            reason = Loc.Instance.Get("RoutingEditor_EmptyPath");
            return false;
        }

        if (System.IO.Path.GetFileName(norm) == "svchost.exe")
        {
            reason = Loc.Instance.Get("RoutingEditor_SvchostTooBroad");
            return false;
        }

        // The shared WebView2 host runs the networking for every WebView2 app; a rule on it (or its runtime
        // folder) tunnels them all. A specific WebView2 app is matched via its own process tree, so add that
        // application instead (#205).
        if (System.IO.Path.GetFileName(norm) == "msedgewebview2.exe"
            || norm.Contains("\\edgewebview\\", StringComparison.Ordinal))
        {
            reason = Loc.Instance.Get("RoutingEditor_WebViewHostTooBroad");
            return false;
        }

        if (norm.Length <= 2
            || norm.EndsWith("\\windows", StringComparison.Ordinal)
            || norm.Contains("\\windows\\system32", StringComparison.Ordinal)
            || norm.Contains("\\windows\\syswow64", StringComparison.Ordinal))
        {
            reason = Loc.Instance.Get("RoutingEditor_PathTooBroad");
            return false;
        }

        return true;
    }

    /// <summary>
    /// A suggested file name when exporting this list.
    /// </summary>
    public string SuggestedFileName => string.IsNullOrWhiteSpace(Name) ? "routing.txt" : $"{Name.Trim()}-routing.txt";

    /// <summary>
    /// Serialises this list to a portable blob for copy / save / QR (role-tagged, so the buckets round-trip; the
    /// traffic options travel with it, like they do in a bundle).
    /// </summary>
    public string BuildTransferPayload() =>
        PortableTransfer.EncodeRouting(Name, AllRoleTokens(), new PortableTransfer.RoutingOptions(AllUdpActive, GlobalProxyActive));

    // All buckets as role-tagged tokens ("proxy|geosite:x", "block|domain:y").
    private IReadOnlyList<string> AllRoleTokens()
    {
        var all = new List<string>(TotalRules);
        all.AddRange(ProxyRules.Select(r => $"proxy|{r}"));
        all.AddRange(DirectRules.Select(r => $"direct|{r}"));
        all.AddRange(BlockRules.Select(r => $"block|{r}"));
        return all;
    }

    // Transfer card mode: QR image vs raw payload text; both share the copy / paste / load / save actions.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransferText))]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    [NotifyPropertyChangedFor(nameof(TransferReady))]
    private bool _isTransferQr = true;

    public bool IsTransferText => !IsTransferQr;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    [NotifyPropertyChangedFor(nameof(TransferReady))]
    private Bitmap? _routingQrImage;

    // Set once a QR build has run, so the too-large notice stays hidden before the first generation.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    private bool _qrAttempted;

    /// <summary>
    /// QR tab active, a build was attempted, and the payload was too large to encode.
    /// </summary>
    public bool QrUnavailable => IsTransferQr && QrAttempted && RoutingQrImage is null;

    /// <summary>
    /// Raw transfer payload shown in the Config tab; refreshed as the list changes.
    /// </summary>
    public string TransferText => BuildTransferPayload();

    /// <summary>
    /// Whether the open form can be copied, saved or sent: the text always, the QR once it is drawn.
    /// </summary>
    public bool TransferReady => IsTransferText || RoutingQrImage is not null;

    /// <summary>
    /// Whether the platform hands an export to another application.
    /// </summary>
    public bool CanSendExport => PlatformExportHost.CanSend;

    [RelayCommand]
    private void ShowTransferQr()
    {
        IsTransferQr = true;
        _ = BuildQrAsync();
    }

    [RelayCommand]
    private void ShowTransferText()
    {
        IsTransferQr = false;
    }

    /// <summary>
    /// Rebuilds the export payload / QR for the current list (called when the Export section is opened).
    /// </summary>
    public void EnsureTransfer() => RefreshTransfer();

    // Keeps the QR / payload text in sync with the current list.
    private void RefreshTransfer()
    {
        OnPropertyChanged(nameof(TransferText));
        if (IsTransferQr)
        {
            _ = BuildQrAsync();
        }
    }

    // Discards a stale QR build.
    private int _qrBuildToken;

    // Builds the QR for the current payload and records the attempt.
    private async Task BuildQrAsync()
    {
        var token = ++_qrBuildToken;
        var payload = BuildTransferPayload();
        var image = await Task.Run(() => TryBuildQr(payload));
        if (token != _qrBuildToken)
        {
            return;
        }

        RoutingQrImage = image;
        QrAttempted = true;
    }

    private static Bitmap? TryBuildQr(string payload)
    {
        try
        {
            return QrCodec.Generate(payload);
        }
        catch
        {
            // Payload too large for a QR.
            return null;
        }
    }

    /// <summary>
    /// Replaces this list's name + rules from an imported blob; the result auto-saves.
    /// </summary>
    public bool ApplyImport(string text) => ApplyImport(text, out _);

    /// <summary>
    /// Replaces this list's name + rules from an imported blob and reports the traffic options it carried; the
    /// options belong to the sibling settings editor, so the caller applies them.
    /// </summary>
    public bool ApplyImport(string text, out PortableTransfer.RoutingOptions? options)
    {
        if (!PortableTransfer.TryDecodeRouting(text, out var name, out var importedRules, out options))
        {
            StatusMessage = Loc.Instance.Get("RoutingEditor_NotARoutingList");
            return false;
        }

        // Seeds the whole replacement (name + every bucket).
        _seeding = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                Name = name;
            }

            ProxyRules.Clear();
            DirectRules.Clear();
            BlockRules.Clear();
            foreach (var rule in importedRules)
            {
                var (role, plain) = SplitRoleToken(rule);
                BucketFor(role).Add(plain);
            }
        }
        finally
        {
            _seeding = false;
        }

        RebuildRuleItems();
        _ = ApplySuggestionFilterAsync();
        MarkDirty();
        FireAutoSave();

        StatusMessage = Name.Trim().Length == 0
            ? Loc.Instance.Get("RoutingEditor_ImportedRulesNeedName", importedRules.Count)
            : Loc.Instance.Get("RoutingEditor_ImportedRules", importedRules.Count);
        return true;
    }

    partial void OnNameChanged(string value)
    {
        MarkDirty();
    }

    private static readonly string[] KnownPrefixes = ["geosite:", "geoip:", "domain:", "cidr:", "app:"];

    private static string Normalize(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            return t;
        }

        // Strip a URL to its host; leave a known rule prefix untouched.
        var schemeIdx = t.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            return $"domain:{StripHost(t[(schemeIdx + 3)..])}";
        }

        if (HasKnownPrefix(t))
        {
            return t;
        }

        // An address or a network goes to cidr:; anything else is a host -> domain:.
        return t.Contains('/') || IPAddress.TryParse(t, out _) ? $"cidr:{t}" : $"domain:{StripHost(t)}";
    }

    // Drops the leading www. and anything past the host.
    private static string StripHost(string s)
    {
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            s = s[4..];
        }

        var cut = s.AsSpan().IndexOfAny("/?#:".AsSpan());
        return cut < 0 ? s : s[..cut];
    }

    private static bool HasKnownPrefix(string t)
    {
        foreach (var prefix in KnownPrefixes)
        {
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A running-app or service match for the add-row autocomplete. Display is what the box shows; Token is the
/// app: rule added when picked.
/// </summary>
internal sealed record AppCandidate(string Display, string Token)
{
    public override string ToString() => Display;
}
