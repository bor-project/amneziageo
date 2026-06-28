using System;
using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Editor for a shared routing list: name + rules (geo categories or manual domains / cidrs). Edits are
/// persisted automatically - a short debounce after the last change saves the list through the agent, so
/// there is no "Сохранить" button. The share QR is no longer inline (it lived in the edit surface, and
/// swapping the Image stole input focus); it is shown on demand in a separate dialog (BuildTransferPayload
/// feeds QrDialog).
/// </summary>
internal sealed partial class RoutingListEditorViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly Action<long>? _onSaved;
    private readonly DispatcherTimer _autoSaveTimer;
    private long _id;

    // All geo category tokens from the agent's last list-geo response, unfiltered. GeoSuggestions is derived
    // from this set minus the rules already in the list, sorted by name - recomputed locally (no extra IPC)
    // whenever Rules changes, so an added rule drops out of the suggestions immediately and a removed one
    // comes back.
    private readonly List<string> _allGeoTokens = [];

    // Auto-save is suppressed while the editor is constructed and its initial rules are loaded, so seeding
    // Name/Rules does not immediately re-save unchanged data (and churn the snapshot). Cleared when LoadAsync
    // finishes; user edits after that schedule a debounced save.
    private bool _suppressAutoSave = true;

    // Consecutive failed auto-saves; bounds retrying a transient agent rejection so a single NAK does not
    // strand the edit, without spinning forever on a persistent failure.
    private int _saveFailures;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _ruleInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // Per-app tunneling add-row (#68). The source mode is chosen from a dropdown: "running" turns the input
    // into an autocomplete over the agent's running app/service list; "folder"/"file" are one-shot dialogs
    // raised from the view. App entries are stored as app: rule tokens in the same Rules collection as geo.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppPickerActive))]
    private string _appMode = "running";

    [ObservableProperty]
    private string _appInput = string.Empty;

    [ObservableProperty]
    private AppCandidate? _appSelected;

    [ObservableProperty]
    private string _appHint = string.Empty;

    /// <summary>
    /// ctor used when creating a fresh routing list.
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, Action<long>? onSaved = null)
        : this(connection, 0, string.Empty, onSaved)
    {
        IsNew = true;
    }

    /// <summary>
    /// ctor used when editing an existing routing list (rules loaded via LoadAsync).
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, long id, string name, Action<long>? onSaved = null)
    {
        _connection = connection;
        _onSaved = onSaved;
        _id = id;
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _autoSaveTimer.Tick += OnAutoSaveTick;
        Rules.CollectionChanged += (_, _) =>
        {
            // Re-derive the suggestions (the rule just added/removed should disappear from / reappear in the
            // dropdown) and queue the debounced save.
            ApplySuggestionFilter();
            ScheduleAutoSave();
        };
        Name = name;
    }

    /// <summary>
    /// True when this editor is creating a new list rather than editing an existing one.
    /// </summary>
    public bool IsNew { get; }

    /// <summary>
    /// The persisted list id (0 until a new list is first saved).
    /// </summary>
    public long Id => _id;

    /// <summary>
    /// The rules of this list as rule tokens (geosite:openai etc).
    /// </summary>
    public ObservableCollection<string> Rules { get; } = [];

    /// <summary>
    /// Geo category suggestions for the rule input, fetched from the agent.
    /// </summary>
    public ObservableCollection<string> GeoSuggestions { get; } = [];

    /// <summary>
    /// App/service matches for the per-app add-row's autocomplete (running mode), fetched from the agent.
    /// </summary>
    public ObservableCollection<AppCandidate> AppSuggestions { get; } = [];

    /// <summary>Whether the per-app add-row is in a list-pick mode (running or installed): the autocomplete
    /// and «Добавить» are active and the hint is shown. Folder/file picks are one-shot dialogs instead.</summary>
    public bool IsAppPickerActive => AppMode is "running" or "installed";

    /// <summary>
    /// Fetches geo category suggestions and (for existing lists) the current rules.
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

        // Seeding done; edits from here on auto-save.
        _suppressAutoSave = false;
    }

    /// <summary>
    /// Re-fetches the geo category suggestions from the agent, replacing the current set. Called when the
    /// set of geo sources changes (a source finished downloading, was added or removed) so the rule search
    /// reflects the new categories without reopening the editor.
    /// </summary>
    public async Task RefreshSuggestionsAsync()
    {
        var response = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListGeo, []));
        if (!response.Ok)
        {
            return;
        }

        // Cache the full set, then derive the visible suggestions. ApplySuggestionFilter swaps GeoSuggestions
        // in one go, so the list is never momentarily empty across the await and overlapping refreshes do not
        // interleave a half-built set.
        var tokens = response.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _allGeoTokens.Clear();
        _allGeoTokens.AddRange(tokens);
        ApplySuggestionFilter();
    }

    /// <summary>
    /// Rebuilds <see cref="GeoSuggestions"/> from the cached geo tokens: drops the rules already in the list
    /// and sorts by name. Runs locally (no IPC), so it is cheap to re-apply on every Rules change.
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
    /// Saves the list (insert or update) through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        var trimmed = Name.Trim();
        if (trimmed.Length == 0)
        {
            StatusMessage = "Введите имя правила";
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

            StatusMessage = ack.Ok ? "Сохранено" : ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Deletes the list through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> DeleteAsync()
    {
        CancelPendingSave();
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

    /// <summary>
    /// Cancels a pending debounced auto-save without persisting it (used when the list is being deleted).
    /// </summary>
    public void CancelPendingSave() => _autoSaveTimer.Stop();

    /// <summary>
    /// Called when the editor is detached (list switch / close / profile change / disconnect). A persisted
    /// list (Id != 0) with a queued edit is flushed so navigating away does not lose it; an un-persisted draft
    /// (Id == 0) is simply abandoned, so a half-made "+ Новый список" leaves no orphan list behind.
    /// </summary>
    public void DetachAutoSave()
    {
        var hadPending = _autoSaveTimer.IsEnabled;
        _autoSaveTimer.Stop();
        if (hadPending && _id != 0 && Name.Trim().Length != 0)
        {
            // Persist the last edit (fire-and-forget). The list is already bound, so no _onSaved is needed.
            _ = SaveAsync();
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
    /// Clears all entries of this list at once, for rebuilding a large list from scratch (auto-saves).
    /// </summary>
    [RelayCommand]
    private void ClearRules()
    {
        Rules.Clear();
    }

    // --- Per-app tunneling (#68): the add-row's "running" autocomplete + the shared token-add path ---

    /// <summary>Switches the add-row to "running" mode and (re)loads the running app/service matches.</summary>
    public async Task EnterRunningModeAsync()
    {
        AppMode = "running";
        AppHint = "Начните вводить имя — выберите запущенное приложение или службу из списка.";
        await LoadRunningAsync();
    }

    /// <summary>Switches the add-row to "installed" mode and loads installed apps from the Uninstall registry.</summary>
    public async Task EnterInstalledModeAsync()
    {
        AppMode = "installed";
        AppHint = "Начните вводить имя — выберите установленное приложение из списка.";
        AppInput = string.Empty;
        AppSelected = null;
        // Registry enumeration is synchronous I/O; run it off the UI thread, then publish on return.
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
                display = $"{label} · служба";
            }
            else
            {
                // Default an app to its containing folder so sibling helpers and versioned subfolders match.
                var dir = System.IO.Path.GetDirectoryName(value);
                token = !string.IsNullOrEmpty(dir) ? $"app:dir={dir}" : $"app:path={value}";
                display = $"{label} · приложение";
            }

            AppSuggestions.Add(new AppCandidate(display, token));
        }
    }

    /// <summary>Adds the app/service picked in the running-mode autocomplete as an app: rule.</summary>
    [RelayCommand]
    private void AddApp()
    {
        var token = AppSelected?.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusMessage = "Выберите приложение или службу из списка.";
            return;
        }

        AddAppToken(token);
        AppInput = string.Empty;
        AppSelected = null;
    }

    /// <summary>
    /// Adds an app matcher token (app:path=/dir=/svc=) after a safety check. Called by the view for folder
    /// and file picks, and by <see cref="AddApp"/> for a running-mode pick.
    /// </summary>
    public void AddAppToken(string token)
    {
        if (!IsAppMatcherSafe(token, out var reason))
        {
            StatusMessage = reason;
            return;
        }

        if (!Rules.Contains(token))
        {
            Rules.Add(token);
        }
    }

    // Rejects matchers that would tunnel far more than one app: a shared service host (svchost.exe) or a
    // path so broad (a drive root, the Windows / System32 tree) it would capture much of the system.
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
            reason = "Пустой путь.";
            return false;
        }

        if (System.IO.Path.GetFileName(norm) == "svchost.exe")
        {
            reason = "svchost.exe — общий хост служб, затуннелит много чужого. Выберите службу из списка.";
            return false;
        }

        if (norm.Length <= 2
            || norm.EndsWith("\\windows", StringComparison.Ordinal)
            || norm.Contains("\\windows\\system32", StringComparison.Ordinal)
            || norm.Contains("\\windows\\syswow64", StringComparison.Ordinal))
        {
            reason = "Слишком широкий путь — выберите конкретную папку приложения.";
            return false;
        }

        return true;
    }

    /// <summary>A suggested file name when exporting this list.</summary>
    public string SuggestedFileName => string.IsNullOrWhiteSpace(Name) ? "routing.txt" : $"{Name.Trim()}-routing.txt";

    /// <summary>
    /// Serialises this list (name + rules) to a portable blob for copy / save / QR - the same share flow a
    /// config has.
    /// </summary>
    public string BuildTransferPayload() => PortableTransfer.EncodeRouting(Name, Rules);

    /// <summary>
    /// Replaces this list's name + rules from an imported blob. Returns whether the text was a recognisable
    /// routing-list blob; the result auto-saves.
    /// </summary>
    public bool ApplyImport(string text)
    {
        if (!PortableTransfer.TryDecodeRouting(text, out var name, out var importedRules))
        {
            StatusMessage = "Не похоже на список маршрутизации.";
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
            ? $"Импортировано правил: {importedRules.Count}. Введите имя, чтобы сохранить."
            : $"Импортировано правил: {importedRules.Count}.";
        return true;
    }

    partial void OnNameChanged(string value)
    {
        ScheduleAutoSave();
    }

    // (Re)start the debounce so a save fires a short while after the last edit. No-op while seeding.
    private void ScheduleAutoSave()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async void OnAutoSaveTick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        await AutoSaveAsync();
    }

    private async Task AutoSaveAsync()
    {
        // Nothing to persist until the list has a name; stay quiet (no nag) while it is still blank.
        if (Name.Trim().Length == 0)
        {
            return;
        }

        // A save is still in flight: retry after the debounce so the latest edit is not dropped.
        if (IsBusy)
        {
            ScheduleAutoSave();
            return;
        }

        // Capture before the save: _onSaved must fire exactly once, when a new list (id=0) is first
        // persisted. Subsequent auto-saves for name or rule edits on an already-persisted list must
        // not call _onSaved — the callback triggers profile assignment / snapshot sync on the host
        // side, which can re-create the editor and close the inline name field while the user types.
        var wasNew = _id == 0;

        if (await SaveAsync())
        {
            _saveFailures = 0;
            if (wasNew && _id != 0)
            {
                _onSaved?.Invoke(_id);
            }
        }
        else if (++_saveFailures <= 2)
        {
            // A transient agent rejection (the blank-name case already returned above): retry shortly so a
            // single NAK does not strand the edit unpersisted.
            ScheduleAutoSave();
        }
    }

    private static string Normalize(string text)
    {
        if (text.Contains(':'))
        {
            return text;
        }

        return text.Contains('/') ? $"cidr:{text}" : $"domain:{text}";
    }
}

/// <summary>
/// A running-app or service match shown in the per-app add-row autocomplete (#68). The display string is
/// what the box shows and filters on; the token is the app: rule added when picked.
/// </summary>
internal sealed record AppCandidate(string Display, string Token)
{
    public override string ToString() => Display;
}
