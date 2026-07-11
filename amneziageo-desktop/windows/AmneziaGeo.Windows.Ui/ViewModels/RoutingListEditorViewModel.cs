using System;
using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Editor for a shared routing list: name + rules (geo categories or manual domains / cidrs). Edits are held
/// in the buffer and persisted atomically through the agent on the header Save (#143).
/// </summary>
internal sealed partial class RoutingListEditorViewModel : ViewModelBase, IEditScope
{
    private readonly AgentConnection _connection;
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

    // Baseline captured on load / commit; the list is dirty when Name or Rules differ from it (a new draft
    // stays dirty until its first save).
    private string _baseName = string.Empty;
    private List<string> _baseRules = [];

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
    public RoutingListEditorViewModel(AgentConnection connection, Action<long>? onSaved = null)
        : this(connection, 0, string.Empty, onSaved)
    {
        IsNew = true;
    }

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, long id, string name, Action<long>? onSaved = null)
    {
        _connection = connection;
        _onSaved = onSaved;
        _id = id;
        Rules.CollectionChanged += (_, _) =>
        {
            ApplySuggestionFilter();
            MarkDirty();
            FireAutoSave();
        };
        Name = name;
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
    /// The rules of this list as rule tokens (geosite:openai etc).
    /// </summary>
    public ObservableCollection<string> Rules { get; } = [];

    /// <summary>
    /// Geo category suggestions for the rule input, fetched from the agent.
    /// </summary>
    public ObservableCollection<string> GeoSuggestions { get; } = [];

    /// <summary>
    /// App/service matches for the per-app add-row autocomplete (running mode), fetched from the agent.
    /// </summary>
    public ObservableCollection<AppCandidate> AppSuggestions { get; } = [];

    /// <summary>
    /// True when the add-row is in a list-pick mode (running or installed).
    /// </summary>
    public bool IsAppPickerActive => AppMode is "running" or "installed";

    /// <summary>
    /// Watermark for the app add-row input, reflects the selected source mode.
    /// </summary>
    public string AppWatermark => AppMode switch
    {
        "installed" => Loc.Instance.Get("RoutingEditor_AppWatermarkInstalled"),
        _ => Loc.Instance.Get("RoutingEditor_AppWatermarkRunning"),
    };

    /// <summary>
    /// Fetches geo category suggestions and the current rules for an existing list.
    /// </summary>
    public async Task LoadAsync()
    {
        await RefreshSuggestionsAsync();

        if (_id != 0)
        {
            var detail = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetRoutingList, [_id.ToString(CultureInfo.InvariantCulture)]));
            if (detail.Ok)
            {
                foreach (var token in detail.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Rules.Add(token);
                }
            }
        }

        // Seeding done: snapshot the loaded state as the clean baseline; edits from here mark the item dirty.
        _seeding = false;
        CaptureBaseline();

        // Prime the default "running" source so the app picker works without first re-choosing it from the menu.
        await LoadRunningAsync();
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
        ApplySuggestionFilter();
    }

    /// <summary>
    /// Rebuilds GeoSuggestions from cached tokens, dropping rules already in the list.
    /// </summary>
    private void ApplySuggestionFilter()
    {
        var selected = new HashSet<string>(Rules, StringComparer.Ordinal);
        GeoSuggestions.Clear();
        foreach (var token in _allGeoTokens
                     .Where(token => !selected.Contains(token))
                     .OrderBy(token => token, StringComparer.OrdinalIgnoreCase))
        {
            GeoSuggestions.Add(token);
        }
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
        if (Rules.Count == 0)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_AddAtLeastOneEntry");
            return false;
        }

        IsBusy = true;
        try
        {
            var args = new List<string> { _id.ToString(CultureInfo.InvariantCulture), trimmed };
            args.AddRange(Rules);
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
            || !Rules.SequenceEqual(_baseRules, StringComparer.Ordinal);
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public bool CanCommit()
    {
        if (Name.Trim().Length == 0)
        {
            ValidationMessage = Loc.Instance.Get("RoutingEditor_EnterRuleName");
            return false;
        }

        if (Rules.Count == 0)
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
        _baseRules = Rules.ToList();
        MarkDirty();
    }

    /// <inheritdoc />
    public void Revert()
    {
        _seeding = true;
        try
        {
            Name = _baseName;
            Rules.Clear();
            foreach (var rule in _baseRules)
            {
                Rules.Add(rule);
            }

            RuleInput = string.Empty;
            StatusMessage = string.Empty;
            ValidationMessage = string.Empty;
        }
        finally
        {
            _seeding = false;
        }

        ApplySuggestionFilter();
        MarkDirty();
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

        if (Name.Trim().Length == 0 || Rules.Count == 0)
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
        // Run registry enumeration off the UI thread.
        var candidates = await Task.Run(InstalledApps.List);
        AppSuggestions.Clear();
        foreach (var candidate in candidates)
        {
            AppSuggestions.Add(candidate);
        }
    }

    private async Task LoadRunningAsync()
    {
        var response = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListProcesses, []));
        AppSuggestions.Clear();
        if (!response.Ok)
        {
            return;
        }

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
                // Default an app to its containing folder.
                var dir = System.IO.Path.GetDirectoryName(value);
                token = !string.IsNullOrEmpty(dir) ? $"app:dir={dir}" : $"app:path={value}";
                display = Loc.Instance.Get("RoutingEditor_AppKindApplication", label);
            }

            AppSuggestions.Add(new AppCandidate(display, token));
        }
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
    /// Serialises this list to a portable blob for copy / save / QR.
    /// </summary>
    public string BuildTransferPayload() => PortableTransfer.EncodeRouting(Name, Rules);

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

        if (!string.IsNullOrWhiteSpace(name))
        {
            Name = name;
        }

        Rules.Clear();
        foreach (var rule in importedRules)
        {
            Rules.Add(rule);
        }

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
