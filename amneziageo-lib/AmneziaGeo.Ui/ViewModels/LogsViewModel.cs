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
/// Logs screen: a view/settings segmented toggle. View reads a window of the selected log table from the DB on
/// demand (nothing is cached across a page change); Settings tunes capture verbosity (ageo) or the routing-log
/// switch (routes).
/// </summary>
internal sealed partial class LogsViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;

    // Verbosity shown for the agent capture level when the agent reports nothing usable.
    private const string DefaultCaptureLevel = "info";

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

    // --- Segmented mode: viewer vs settings ---

    [ObservableProperty]
    private bool _isSettingsMode;

    /// <summary>
    /// Whether the viewer pane is showing (the inverse of settings mode).
    /// </summary>
    public bool IsViewMode => !IsSettingsMode;

    partial void OnIsSettingsModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsViewMode));
    }

    [RelayCommand]
    private void ShowView()
    {
        IsSettingsMode = false;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        IsSettingsMode = true;
    }

    // --- Log type (shared: viewer + settings) ---

    /// <summary>
    /// The selectable log tables. The tokens are the same in every language.
    /// </summary>
    public ObservableCollection<string> LogTypes { get; } = ["ageo", "routes"];

    [ObservableProperty]
    private string _selectedLogType = "ageo";

    /// <summary>
    /// Whether the viewer is on the agent log (which carries a level; the routing log does not).
    /// </summary>
    public bool IsAgentLog => SelectedLogType == "ageo";

    partial void OnSelectedLogTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAgentLog));
        ResetAndReload();
    }

    // --- Capture level (settings, ageo): none disables capture entirely ---

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

    // --- Routing log toggle (settings, routes) ---

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

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasLogs;

    // Whether a window load is in flight; shows the loader in place of the body (not raised for the tail poll).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    /// <summary>
    /// Whether the log body is shown: there is content and no load is in flight.
    /// </summary>
    public bool ShowBody => HasLogs && !IsLoading;

    /// <summary>
    /// Whether the empty hint is shown: no content and no load in flight.
    /// </summary>
    public bool ShowEmpty => !HasLogs && !IsLoading;

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
        LogText = string.Empty;
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
        if (IsActive && IsViewMode && LogFollow && _cursor is null && _cursorStack.Count == 0)
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
        IpcAck ack;
        try
        {
            ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpExportLog, [SelectedLogType]));
        }
        catch
        {
            return;
        }

        if (!ack.Ok)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, ack.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpLogClient, [$"log export write failed: {ex.Message}"]));
        }
    }

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
        LogText = payload.Lines.Count > 0 ? string.Join('\n', payload.Lines) : string.Empty;
        HasLogs = payload.Lines.Count > 0;
        SearchMatchCount = string.IsNullOrWhiteSpace(SearchQuery) ? 0 : payload.MatchCount;
        LogCanPageOlder = payload.HasOlder;
        LogCanPageNewer = beforeId is not null;
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
