using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Logs screen: one viewer over the selected source. A window of the log table is read from the DB on demand
/// (nothing is cached across a page change); the header carries what the source records - capture verbosity
/// (ageo) or the routing-log switch (routes).
/// </summary>
internal sealed partial class LogsViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;

    // Verbosity shown for the agent capture level when the agent reports nothing usable.
    private const string DefaultCaptureLevel = "error";

    // Rows requested per window.
    private const int LogLimit = 400;

    private static readonly JsonSerializerOptions LogJson = new() { PropertyNameCaseInsensitive = true };

    private bool _suppressSettingPush;

    // Current window upper bound (null = live tail) and the cursor stack that walks back toward the tail.
    private long? _cursor;
    private readonly List<long?> _cursorStack = [];
    private long _windowFirstId;

    // Coalesces overlapping loads: the newest requested cursor wins.
    private bool _loadBusy;
    private bool _reloadQueued;
    private long? _queuedCursor;
    private bool _queuedShowLoader;

    // Polls the live tail while the section is open on the viewer with follow on (snapshots no longer carry logs).
    private readonly DispatcherTimer _pollTimer;

    /// <summary>
    /// ctor
    /// </summary>
    public LogsViewModel(IAgentConnection connection)
    {
        _connection = connection;
        Loc.Instance.CultureChanged += OnCultureChanged;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => OnPollTick();
    }

    private void OnCultureChanged()
    {
        OnPropertyChanged(nameof(SearchSummary));
    }

    /// <summary>
    /// Whether the logs section is the one currently shown; gates the tail poll.
    /// </summary>
    public bool IsActive { get; private set; }

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    private bool _isCompact;

    partial void OnIsCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStoredText));
        OnPropertyChanged(nameof(ShowLiveText));
        OnPropertyChanged(nameof(ShowStoredCards));
        OnPropertyChanged(nameof(ShowLiveCards));
        Render();
    }

    // --- Log type ---

    /// <summary>
    /// The live source: what the tunnel carries right now, asked of the agent instead of a stored table.
    /// </summary>
    public const string LiveType = "active";

    /// <summary>
    /// The selectable sources. The tokens are the same in every language.
    /// </summary>
    public ObservableCollection<string> LogTypes { get; } = ["ageo", "routes", LiveType];

    [ObservableProperty]
    private string _selectedLogType = "ageo";

    /// <summary>
    /// Whether the viewer is on the agent log (which carries a level; the routing log does not).
    /// </summary>
    public bool IsAgentLog => SelectedLogType == "ageo";

    /// <summary>
    /// Whether the viewer is on the routing log.
    /// </summary>
    public bool IsRouteLog => SelectedLogType == "routes";

    /// <summary>
    /// Whether the viewer is on what the tunnel carries right now.
    /// </summary>
    public bool IsLiveLog => SelectedLogType == LiveType;

    /// <summary>
    /// Whether the viewer is on a stored table, which is what can be searched, paged, cleared and exported.
    /// </summary>
    public bool IsStoredLog => !IsLiveLog;

    partial void OnSelectedLogTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAgentLog));
        OnPropertyChanged(nameof(IsRouteLog));
        OnPropertyChanged(nameof(IsLiveLog));
        OnPropertyChanged(nameof(IsStoredLog));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowStoredText));
        OnPropertyChanged(nameof(ShowLiveText));
        OnPropertyChanged(nameof(ShowStoredCards));
        OnPropertyChanged(nameof(ShowLiveCards));
        ClearView();
        ResetAndReload();
    }

    // --- Capture level (ageo): none disables capture entirely ---

    /// <summary>
    /// Agent-log capture level options; none stops logging.
    /// </summary>
    public ObservableCollection<string> CaptureLevels { get; } = ["none", "error", "warning", "info", "debug", "trace"];

    [ObservableProperty]
    private string _captureLevel = DefaultCaptureLevel;

    partial void OnCaptureLevelChanged(string value)
    {
        if (!_suppressSettingPush)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, ["log-level", value]));
        }
    }

    // --- Routing log toggle (routes) ---

    [ObservableProperty]
    private bool _routeLogEnabled;

    partial void OnRouteLogEnabledChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, ["route-log", value ? "on" : "off"]));
        }
    }

    // --- Search ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private string _searchQuery = string.Empty;

    partial void OnSearchQueryChanged(string value)
    {
        // Search updates as you type; no loader (it would flash the body on every keystroke).
        ResetAndReload(showLoader: false);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private int _searchMatchCount;

    /// <summary>
    /// Human-readable match count for the log search box; empty when no query is active.
    /// </summary>
    public string SearchSummary => string.IsNullOrWhiteSpace(SearchQuery)
        ? string.Empty
        : Loc.Instance.Get("MainVm_LogSearchMatches", SearchMatchCount);

    // --- Log body ---

    // What the last read brought, kept to render again when the window changes side.
    private IReadOnlyList<string> _lines = [];
    private SessionReport _carried = SessionReport.Empty;

    [ObservableProperty]
    private string _logText = string.Empty;

    /// <summary>
    /// Stored rows as cards, which is what a narrow window shows instead of the text.
    /// </summary>
    public ObservableCollection<LogEntryItem> Entries { get; } = [];

    /// <summary>
    /// Destinations as cards, which is what a narrow window shows instead of the text.
    /// </summary>
    public ObservableCollection<LiveRowItem> LiveRows { get; } = [];

    // What the tunnel carries in one line, above the rows it counts.
    [ObservableProperty]
    private string _liveSummary = string.Empty;

    // Held Ctrl: the viewer stops taking new rows, so a selection survives long enough to be copied.
    [ObservableProperty]
    private bool _isFrozen;

    partial void OnIsFrozenChanged(bool value)
    {
        if (!value)
        {
            OnPollTick();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowStoredText))]
    [NotifyPropertyChangedFor(nameof(ShowLiveText))]
    [NotifyPropertyChangedFor(nameof(ShowStoredCards))]
    [NotifyPropertyChangedFor(nameof(ShowLiveCards))]
    private bool _hasLogs;

    // Whether a window load is in flight; shows the loader in place of the body (not raised for the tail poll).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowStoredText))]
    [NotifyPropertyChangedFor(nameof(ShowLiveText))]
    [NotifyPropertyChangedFor(nameof(ShowStoredCards))]
    [NotifyPropertyChangedFor(nameof(ShowLiveCards))]
    private bool _isLoading;

    /// <summary>
    /// Whether the log body is shown: there is content and no load is in flight.
    /// </summary>
    public bool ShowBody => HasLogs && !IsLoading;

    /// <summary>
    /// Whether the stored rows are shown as text, which is what a wide window carries.
    /// </summary>
    public bool ShowStoredText => ShowBody && !IsCompact && IsStoredLog;

    /// <summary>
    /// Whether the destinations are shown as text, which is what a wide window carries.
    /// </summary>
    public bool ShowLiveText => ShowBody && !IsCompact && IsLiveLog;

    /// <summary>
    /// Whether the stored rows are shown as cards, which is what a narrow window carries.
    /// </summary>
    public bool ShowStoredCards => ShowBody && IsCompact && IsStoredLog;

    /// <summary>
    /// Whether the destinations are shown as cards, which is what a narrow window carries.
    /// </summary>
    public bool ShowLiveCards => ShowBody && IsCompact && IsLiveLog;

    /// <summary>
    /// Whether the empty hint is shown: no content and no load in flight. The live source says the same thing
    /// in its summary line, so it does not carry the hint as well.
    /// </summary>
    public bool ShowEmpty => !HasLogs && !IsLoading && !IsLiveLog;

    // Whether the view snaps to the live tail on each poll.
    [ObservableProperty]
    private bool _logFollow = true;

    partial void OnLogFollowChanged(bool value)
    {
        if (value)
        {
            ResetAndReload();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PageOlderCommand))]
    private bool _logCanPageOlder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PageNewerCommand))]
    private bool _logCanPageNewer;

    /// <summary>
    /// Mirrors the agent's capture settings (without echoing them back). Does not touch the viewer body.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        _suppressSettingPush = true;
        CaptureLevel = KnownCaptureLevel(snapshot.LogLevel);
        RouteLogEnabled = snapshot.RouteLog;
        _suppressSettingPush = false;
    }

    /// <summary>
    /// Marks the section active or not; opening it loads the live tail and starts the poll, leaving it frees it.
    /// </summary>
    public void SetActive(bool active)
    {
        if (active == IsActive)
        {
            return;
        }

        IsActive = active;
        if (active)
        {
            ResetAndReload();
            _pollTimer.Start();
        }
        else
        {
            _pollTimer.Stop();
            ClearView();
        }
    }

    /// <summary>
    /// Drops the viewer state so a reconnect starts clean.
    /// </summary>
    public void Reset()
    {
        IsActive = false;
        _pollTimer.Stop();
        ClearView();
    }

    private void ClearView()
    {
        _cursor = null;
        _cursorStack.Clear();
        _windowFirstId = 0;
        _lines = [];
        _carried = SessionReport.Empty;
        LogText = string.Empty;
        LiveSummary = string.Empty;
        Entries.Clear();
        LiveRows.Clear();
        HasLogs = false;
        IsLoading = false;
        LogCanPageOlder = false;
        LogCanPageNewer = false;
    }

    private void ResetAndReload(bool showLoader = true)
    {
        _cursor = null;
        _cursorStack.Clear();
        // Re-arm follow (field, not property, to avoid re-entering ResetAndReload) on every jump to the tail.
#pragma warning disable MVVMTK0034
        _logFollow = true;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(LogFollow));
        Reload(null, showLoader);
    }

    private void OnPollTick()
    {
        if (!IsActive || !LogFollow || IsFrozen)
        {
            return;
        }

        // The live source has no history to walk, so a tick always re-reads it.
        if (IsLiveLog || (_cursor is null && _cursorStack.Count == 0))
        {
            Reload(null, showLoader: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPageOlder))]
    private void PageOlder()
    {
        if (_loadBusy)
        {
            return;
        }

        // Browsing history: stop the poll from snapping back to the live tail.
        LogFollow = false;
        _cursorStack.Add(_cursor);
        _cursor = _windowFirstId;
        Reload(_cursor, showLoader: true);
    }

    private bool CanPageOlder => LogCanPageOlder;

    [RelayCommand(CanExecute = nameof(CanPageNewer))]
    private void PageNewer()
    {
        if (_loadBusy)
        {
            return;
        }

        if (_cursorStack.Count > 0)
        {
            _cursor = _cursorStack[^1];
            _cursorStack.RemoveAt(_cursorStack.Count - 1);
            if (_cursor is null)
            {
                // Back at the newest window: resume following the live tail (OnLogFollowChanged reloads it).
                LogFollow = true;
            }
            else
            {
                Reload(_cursor, showLoader: true);
            }
        }
        else
        {
            LogFollow = true;
        }
    }

    private bool CanPageNewer => LogCanPageNewer;

    [RelayCommand]
    private async Task ClearLog()
    {
        if (IsLiveLog)
        {
            return;
        }

        try
        {
            await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpClearLog, [SelectedLogType]));
        }
        catch
        {
            return;
        }

        ResetAndReload();
    }

    /// <summary>
    /// Exports the whole selected table to the file the view chose: the agent renders the text, the UI writes
    /// it under the user account.
    /// </summary>
    public async Task ExportToAsync(string path)
    {
        if (await BuildExportTextAsync() is not { } text)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpLogClient, [$"log export write failed: {ex.Message}"]));
        }
    }

    /// <summary>
    /// Renders the whole selected table through the agent; null when the agent did not answer.
    /// </summary>
    /// <summary>
    /// What the viewer shows right now, as text for the clipboard.
    /// </summary>
    public string VisibleText()
    {
        if (IsLiveLog)
        {
            return LiveSummary + "\n\n" + SessionRows.Text(_carried);
        }

        return LogText.Length > 0 ? LogText : string.Join('\n', _lines);
    }

    public async Task<string?> BuildExportTextAsync()
    {
        if (IsLiveLog)
        {
            // Nothing is stored behind the live source: what travels is what is on screen.
            return VisibleText();
        }

        IpcAck ack;
        try
        {
            ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpExportLog, [SelectedLogType]));
        }
        catch
        {
            return null;
        }

        return ack.Ok ? ack.Message : null;
    }

    /// <summary>
    /// Whether the platform hands an export to another application.
    /// </summary>
    public bool CanSendExport => PlatformExportHost.CanSend;

    // Queues a load of the window ending at beforeId (null = tail); coalesces so the newest request wins.
    // showLoader marks a user-driven load (shows the loader); the background tail poll passes false.
    private void Reload(long? beforeId, bool showLoader)
    {
        _queuedCursor = beforeId;
        _queuedShowLoader |= showLoader;
        _reloadQueued = true;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_loadBusy)
        {
            return;
        }

        _loadBusy = true;
        try
        {
            while (_reloadQueued)
            {
                _reloadQueued = false;
                var showLoader = _queuedShowLoader;
                _queuedShowLoader = false;
                var cursor = _queuedCursor;
                if (showLoader)
                {
                    IsLoading = true;
                }

                try
                {
                    await LoadAsync(cursor);
                }
                finally
                {
                    if (showLoader)
                    {
                        IsLoading = false;
                    }
                }
            }
        }
        finally
        {
            _loadBusy = false;
            IsLoading = false;
        }
    }

    private async Task LoadAsync(long? beforeId)
    {
        if (IsLiveLog)
        {
            await LoadCarriedAsync();
            return;
        }

        var type = SelectedLogType;
        var args = new List<string>
        {
            type,
            LogLimit.ToString(CultureInfo.InvariantCulture),
            (beforeId ?? 0).ToString(CultureInfo.InvariantCulture),
            type == "ageo" ? "trace" : string.Empty,
            SearchQuery ?? string.Empty,
        };

        IpcAck ack;
        try
        {
            ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpReadLog, args));
        }
        catch
        {
            return;
        }

        if (!ack.Ok)
        {
            return;
        }

        LogWindowPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LogWindowPayload>(ack.Message, LogJson);
        }
        catch (JsonException)
        {
            return;
        }

        if (payload is null)
        {
            return;
        }

        // Section left during the pipe round-trip: drop the stale window so the freed state stays freed.
        if (!IsActive)
        {
            return;
        }

        _windowFirstId = payload.FirstId;
        HasLogs = payload.Lines.Count > 0;
        SearchMatchCount = string.IsNullOrWhiteSpace(SearchQuery) ? 0 : payload.MatchCount;
        LogCanPageOlder = payload.HasOlder;
        LogCanPageNewer = beforeId is not null;
        if (Same(_lines, payload.Lines))
        {
            return;
        }

        _lines = payload.Lines;
        Render();
    }

    // Whether the window came back as it was: the tail poll asks every second and mostly gets the same rows.
    private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Reads what the relay holds right now; the live source has no stored table behind it and no history.
    private async Task LoadCarriedAsync()
    {
        IpcAck ack;
        try
        {
            ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetSessions, []));
        }
        catch
        {
            return;
        }

        if (!ack.Ok || !IsActive || !IsLiveLog)
        {
            return;
        }

        _carried = SessionReport.Parse(ack.Message);
        HasLogs = _carried.Sessions.Count > 0;
        SearchMatchCount = 0;
        LogCanPageOlder = false;
        LogCanPageNewer = false;
        Render();
    }

    // Puts what the viewer holds on screen the way the window is shaped: text when it is wide, cards when narrow.
    private void Render()
    {
        if (IsLiveLog)
        {
            RenderCarried();
            return;
        }

        LiveRows.Clear();
        LiveSummary = string.Empty;
        if (!IsCompact)
        {
            Entries.Clear();
            LogText = _lines.Count > 0 ? string.Join('\n', _lines) : string.Empty;
            return;
        }

        LogText = string.Empty;
        var rows = new List<LogEntryItem>(_lines.Count);
        foreach (var line in _lines)
        {
            rows.Add(LogEntryItem.Parse(line));
        }

        Fill(Entries, rows);
    }

    private void RenderCarried()
    {
        Entries.Clear();
        LiveSummary = SessionRows.Summary(_carried);
        if (!IsCompact)
        {
            LiveRows.Clear();
            LogText = SessionRows.Text(_carried);
            return;
        }

        LogText = string.Empty;
        Fill(LiveRows, SessionRows.Cards(_carried));
    }

    // Replaces a card list row by row: a list rebuilt whole loses the place the reader is at in it.
    private static void Fill<T>(ObservableCollection<T> rows, IReadOnlyList<T> next)
    {
        for (var index = 0; index < next.Count; index++)
        {
            if (index >= rows.Count)
            {
                rows.Add(next[index]);
            }
            else if (!Equals(rows[index], next[index]))
            {
                rows[index] = next[index];
            }
        }

        while (rows.Count > next.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    // Falls an unrecognised token back to the default so the combo never goes null.
    private static string KnownCaptureLevel(string token)
    {
        return token switch
        {
            "none" or "trace" or "debug" or "info" or "warning" or "error" => token,
            _ => DefaultCaptureLevel,
        };
    }

    // OpReadLog ack payload: a window of rendered lines newest first, with the paging cursor and match total.
    private sealed record LogWindowPayload(
        IReadOnlyList<string> Lines,
        long FirstId,
        bool HasOlder,
        int MatchCount);
}
