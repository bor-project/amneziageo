using System;
using System.Collections.ObjectModel;
using System.Globalization;
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
internal sealed partial class RoutingListEditorViewModel : ViewModelBase, IEditScope
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
    private string _ruleInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // Required-field validation ("enter a name", "add at least one entry"), shown in red and cleared on any
    // edit (#2/#3). Kept separate from StatusMessage so import/success notices stay neutral, not red.
    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoading;

    // Per-app tunneling add-row. App entries are stored as app: rule tokens.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppPickerActive))]
    [NotifyPropertyChangedFor(nameof(AppWatermark))]
    private string _appMode = "running";

    [ObservableProperty]
    private string _appInput = string.Empty;

    [ObservableProperty]
    private AppCandidate? _appSelected;

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
        if (_reordering || _seeding)
        {
            return;
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

    /// <summary>
    /// True when at least one rule exists in any bucket.
    /// </summary>
    public bool HasAnyRule => TotalRules > 0;

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
    private string _selectedRole = "proxy";

    /// <summary>
    /// The active bucket's rule tokens (geosite:openai etc), selected by <see cref="SelectedRole"/>.
    /// </summary>
    public ObservableCollection<string> Rules => SelectedRole switch
    {
        "direct" => DirectRules,
        "block" => BlockRules,
        _ => ProxyRules,
    };

    public bool IsProxyRole => SelectedRole == "proxy";

    public bool IsDirectRole => SelectedRole == "direct";

    public bool IsBlockRole => SelectedRole == "block";

    /// <summary>
    /// Localized help line for the active role.
    /// </summary>
    public string RoleHint => SelectedRole switch
    {
        "direct" => Loc.Instance.Get("Main_RoleDirectHint"),
        "block" => Loc.Instance.Get("Main_RoleBlockHint"),
        _ => Loc.Instance.Get("Main_RoleProxyHint"),
    };

    // After the active bucket swaps, refresh the suggestion filter for the newly shown bucket.
    partial void OnSelectedRoleChanged(string value) => _ = ApplySuggestionFilterAsync();

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
    /// App/service matches for the per-app add-row autocomplete (running mode), fetched from the agent.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<AppCandidate> _appSuggestions = [];

    /// <summary>
    /// True when the add-row is in a list-pick mode (running or installed).
    /// </summary>
    public bool IsAppPickerActive => AppMode is "running" or "installed";

    /// <summary>
    /// Whether the per-app add-row is shown; the DEBUG marker gates it while the feature is unstable.
    /// </summary>
    public bool ShowPerAppRouting => AppFeatures.PerAppRouting;

    /// <summary>
    /// Watermark for the app add-row input, reflects the selected source mode.
    /// </summary>
    public string AppWatermark => AppMode switch
    {
        "installed" => Loc.Instance.Get("RoutingEditor_AppWatermarkInstalled"),
        _ => Loc.Instance.Get("RoutingEditor_AppWatermarkRunning"),
    };

    /// <summary>
    /// Re-raises the localized computed labels after a language change.
    /// </summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(AppWatermark));
        OnPropertyChanged(nameof(RoleHint));

        // App suggestions bake a localized kind prefix at load; rebuild them for the new language.
        if (AppSuggestions.Count > 0)
        {
            _ = ReloadAppSuggestionsAsync();
        }
    }

    private Task ReloadAppSuggestionsAsync() => AppMode switch
    {
        "running" => LoadRunningAsync(),
        "installed" => LoadInstalledSuggestionsAsync(),
        _ => Task.CompletedTask,
    };

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
            CaptureBaseline();

            // Prime the default "running" source so the app picker works without first re-choosing it from the menu.
            await LoadRunningAsync();
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

        // Do not persist a rule-less list; the pre-#143 auto-save refused it too (review regression guard).
        if (TotalRules == 0)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_AddAtLeastOneEntry");
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
            || !string.Equals(Name, _baseName, StringComparison.Ordinal)
            || !ProxyRules.SequenceEqual(_baseProxy, StringComparer.Ordinal)
            || !DirectRules.SequenceEqual(_baseDirect, StringComparer.Ordinal)
            || !BlockRules.SequenceEqual(_baseBlock, StringComparer.Ordinal);
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }

        OnPropertyChanged(nameof(HasAnyRule));
        RefreshTransfer();
    }

    /// <inheritdoc />
    public bool CanCommit()
    {
        if (Name.Trim().Length == 0)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_EnterRuleName");
            return false;
        }

        if (TotalRules == 0)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_AddAtLeastOneEntry");
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void CaptureBaseline()
    {
        _baseName = Name;
        _baseProxy = ProxyRules.ToList();
        _baseDirect = DirectRules.ToList();
        _baseBlock = BlockRules.ToList();
        MarkDirty();
    }

    /// <inheritdoc />
    public void Revert()
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
    public async Task<bool> CommitAsync()
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
    /// draft with no name or no rules stays unsaved silently - there is nothing to persist yet.
    /// </summary>
    public async Task AutoSaveAsync()
    {
        if (_seeding || !AutoSave)
        {
            return;
        }

        if (Name.Trim().Length == 0 || TotalRules == 0)
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

    /// <summary>
    /// Fetches the machine's local subnets from the agent and adds them to the Direct bucket.
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

            var subnets = ack.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            SelectedRole = "direct";
            var added = 0;
            foreach (var subnet in subnets)
            {
                var rule = Normalize(subnet);
                if (rule.Length > 0 && !DirectRules.Contains(rule))
                {
                    DirectRules.Add(rule);
                    added++;
                }
            }

            StatusMessage = added > 0
                ? Loc.Instance.Get("RoutingSettings_LocalSubnetsAdded", added)
                : subnets.Length == 0
                    ? Loc.Instance.Get("RoutingSettings_NoActiveLocalSubnets")
                    : Loc.Instance.Get("RoutingSettings_AllLocalSubnetsPresent");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        var text = RuleInput.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var rule = Normalize(text);
        if (!Rules.Contains(rule))
        {
            Rules.Add(rule);
        }

        RuleInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveRule(string rule)
    {
        Rules.Remove(rule);
    }

    /// <summary>
    /// Clears all entries of this list at once.
    /// </summary>
    [RelayCommand]
    private void ClearRules()
    {
        Rules.Clear();
    }

    // Direction of the next sort; the first click sorts ascending.
    private bool _sortDescending = true;

    // Suppresses per-item side effects while a sort reorders the collection in place.
    private bool _reordering;

    /// <summary>
    /// Reorders entries by name, flipping direction on each invocation.
    /// </summary>
    [RelayCommand]
    private void SortRules()
    {
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

        MarkDirty();
        FireAutoSave();
    }

    // Per-app tunneling: running autocomplete + token-add path.

    /// <summary>
    /// Switches the add-row to running mode and reloads the running app/service matches.
    /// </summary>
    public async Task EnterRunningModeAsync()
    {
        AppMode = "running";
        await LoadRunningAsync();
    }

    /// <summary>
    /// Switches the add-row to installed mode and loads installed apps from the Uninstall registry.
    /// </summary>
    public async Task EnterInstalledModeAsync()
    {
        AppMode = "installed";
        AppInput = string.Empty;
        AppSelected = null;
        await LoadInstalledSuggestionsAsync();
    }

    private async Task LoadInstalledSuggestionsAsync()
    {
        // Run registry enumeration off the UI thread.
        AppSuggestions = await Task.Run(InstalledApps.List);
    }

    private async Task LoadRunningAsync()
    {
        var response = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListProcesses, []));
        if (!response.Ok)
        {
            AppSuggestions = [];
            return;
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

        AppSuggestions = candidates;
    }

    /// <summary>
    /// Adds the app/service picked in the running-mode autocomplete as an app: rule.
    /// </summary>
    [RelayCommand]
    private void AddApp()
    {
        var token = AppSelected?.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusMessage = Loc.Instance.Get("RoutingEditor_SelectAppOrService");
            return;
        }

        AddAppToken(token);
        AppInput = string.Empty;
        AppSelected = null;
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

        // Store a portable %ENV% path so the rule survives export to another machine or user.
        token = AmneziaGeo.Ipc.AppPathToken.TokenizeRule(token);
        if (!Rules.Contains(token))
        {
            Rules.Add(token);
        }
    }

    // Rejects matchers that would tunnel far more than one app.
    private static bool IsAppMatcherSafe(string token, out string reason)
    {
        reason = string.Empty;
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
    /// Serialises this list to a portable blob for copy / save / QR (role-tagged, so the buckets round-trip).
    /// </summary>
    public string BuildTransferPayload() => PortableTransfer.EncodeRouting(Name, AllRoleTokens());

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
    private bool _isTransferQr = true;

    public bool IsTransferText => !IsTransferQr;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
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
    public bool ApplyImport(string text)
    {
        if (!PortableTransfer.TryDecodeRouting(text, out var name, out var importedRules))
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

        // A bare CIDR/IP keeps its "/" and goes to cidr:; anything else is a host -> domain:.
        return t.Contains('/') ? $"cidr:{t}" : $"domain:{StripHost(t)}";
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
