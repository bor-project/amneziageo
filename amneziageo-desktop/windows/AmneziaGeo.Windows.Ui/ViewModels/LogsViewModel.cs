using System.Collections.ObjectModel;
using System.Text.Json;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Logs screen: capture verbosity, route-log toggle, and the file-backed journal viewer with search and paging.
/// </summary>
internal sealed partial class LogsViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

    // Verbosity token shown when the agent reports nothing usable.
    private const string DefaultLogLevel = "error";

    // Tail window requested per read (bytes); the agent clamps it.
    private const int LogTailBytes = 262144;

    private static readonly JsonSerializerOptions LogJson = new() { PropertyNameCaseInsensitive = true };

    private bool _suppressSettingPush;
    private IReadOnlyList<string> _logLines = [];

    // Serializes log reads so overlapping heartbeat/user loads never interleave on the pipe.
    private readonly System.Threading.SemaphoreSlim _logReadGate = new(1, 1);

    // Byte offset of the oldest line currently loaded; the anchor for paging further back.
    private long? _logOldestOffset;

    // Once the file-backed viewer has loaded, the 300-line snapshot ring stops feeding the view.
    private bool _logViewerEngaged;

    // Guards against overlapping OpListLogs refreshes (heartbeats retry until the first one succeeds).
    private bool _logListBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public LogsViewModel(AgentConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Whether the logs section is the one currently shown; gates the file-backed viewer's heartbeat re-reads.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Log verbosity options. The tokens are the same in every language.
    /// </summary>
    public ObservableCollection<string> LogLevels { get; } = [DefaultLogLevel, "info", "debug", "trace"];

    [ObservableProperty]
    private string _logLevel = DefaultLogLevel;

    [ObservableProperty]
    private bool _routeLogEnabled;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _hasLogs;

    /// <summary>
    /// On-disk log files offered in the viewer's file picker (agent rolls + routing log), newest first.
    /// </summary>
    public ObservableCollection<LogFileChoice> LogFiles { get; } = [];

    [ObservableProperty]
    private LogFileChoice? _selectedLogFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private int _searchMatchCount;

    // Whether the view snaps to the live tail on each snapshot heartbeat.
    [ObservableProperty]
    private bool _logFollow = true;

    // Whether content exists before the loaded window (enables "load earlier").
    [ObservableProperty]
    private bool _logCanPageOlder;

    /// <summary>
    /// Human-readable match count for the log search box; empty when no query is active.
    /// </summary>
    public string SearchSummary => string.IsNullOrWhiteSpace(SearchQuery)
        ? string.Empty
        : Loc.Instance.Get("MainVm_LogSearchMatches", SearchMatchCount);

    /// <summary>
    /// Applies a status snapshot: mirrors the agent's log settings (without echoing them back) and feeds the view.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        _suppressSettingPush = true;
        LogLevel = KnownLogLevel(snapshot.LogLevel);
        RouteLogEnabled = snapshot.RouteLog;
        _suppressSettingPush = false;

        UpdateLogView(snapshot);
    }

    /// <summary>
    /// Marks the section active or not; opening it loads the on-disk files at once.
    /// </summary>
    public void SetActive(bool active)
    {
        IsActive = active;
        if (active)
        {
            _ = RefreshLogFilesAsync();
        }
    }

    /// <summary>
    /// Drops the file-backed viewer state so a reconnect re-lists cleanly and the snapshot ring feeds the view again.
    /// </summary>
    public void Reset()
    {
        _logViewerEngaged = false;
        _logOldestOffset = null;
        LogCanPageOlder = false;
        LogFiles.Clear();
        SelectedLogFile = null;
        _logLines = [];
        HasLogs = false;
        RebuildLogText();
    }

    // Rebuilds the journal text from the raw lines applying the search query, newest first so the latest
    // activity stays visible at the top without scrolling.
    private void RebuildLogText()
    {
        var query = SearchQuery;
        var hasQuery = !string.IsNullOrWhiteSpace(query);

        var shown = new List<string>();
        foreach (var line in _logLines)
        {
            if (hasQuery && !line.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            shown.Add(line);
        }

        SearchMatchCount = hasQuery ? shown.Count : 0;
        if (shown.Count == 0)
        {
            LogText = string.Empty;
            return;
        }

        shown.Reverse();
        LogText = string.Join('\n', shown);
    }

    // Feeds the log view. Before the file-backed viewer is engaged the 300-line snapshot ring drives it (so
    // the section shows something instantly); once engaged, the on-disk file is the source of truth and the
    // heartbeat re-reads the live tail while the log section is open, follow is on, and the newest file is
    // selected.
    private void UpdateLogView(StatusSnapshot snapshot)
    {
        if (!_logViewerEngaged)
        {
            // Not reading files yet: the 300-line ring drives the view. In the log section, also try to engage
            // the file-backed viewer - it only takes over once OpListLogs actually succeeds, so a failed listing
            // leaves the ring showing instead of a blank panel.
            _logLines = snapshot.Logs ?? [];
            HasLogs = _logLines.Count > 0;
            RebuildLogText();
            if (IsActive)
            {
                _ = RefreshLogFilesAsync();
            }

            return;
        }

        if (IsActive && LogFollow && LogFiles.Count > 0
            && ReferenceEquals(SelectedLogFile, LogFiles[0]) && _logReadGate.CurrentCount > 0)
        {
            _ = LoadLogTailAsync();
        }
    }

    // Loads (or refreshes) the list of on-disk log files and re-reads the tail of the selected one. Engages
    // the file-backed viewer only once the listing succeeds, so a transient IPC failure never strands the
    // viewer with the snapshot ring disabled; heartbeats retry it while the log section stays open.
    private async Task RefreshLogFilesAsync()
    {
        if (_logListBusy)
        {
            return;
        }

        _logListBusy = true;
        try
        {
            IpcAck ack;
            try
            {
                ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListLogs, []));
            }
            catch
            {
                return;
            }

            if (!ack.Ok)
            {
                return;
            }

            List<LogFileChoice>? metas;
            try
            {
                metas = JsonSerializer.Deserialize<List<LogFileChoice>>(ack.Message, LogJson);
            }
            catch (JsonException)
            {
                return;
            }

            if (metas is null || metas.Count == 0)
            {
                return;
            }

            // The file-backed source is now available: flip the latch so the ring stops feeding the view.
            _logViewerEngaged = true;

            var previous = SelectedLogFile?.Name;
            LogFiles.Clear();
            foreach (var meta in metas)
            {
                LogFiles.Add(meta);
            }

            // Keep the same file selected across refreshes; fall back to the newest when it has rolled away.
            var target = (previous is not null ? LogFiles.FirstOrDefault(f => f.Name == previous) : null)
                ?? LogFiles[0];
            var before = SelectedLogFile;
            SelectedLogFile = target;

            // A value-equal reselect (byte-identical newest file) short-circuits the [ObservableProperty]
            // setter, so OnSelectedLogFileChanged never fires; reload the tail explicitly so a section re-open
            // reliably refreshes it.
            if (ReferenceEquals(SelectedLogFile, before))
            {
                _logOldestOffset = null;
                LogCanPageOlder = false;
                await LoadLogTailAsync();
            }
        }
        finally
        {
            _logListBusy = false;
        }
    }

    // Reads the selected log file over IPC: the live tail by default, or the window ending at the oldest
    // loaded offset when paging older. Serialized so heartbeat and user loads never interleave on the pipe.
    private async Task LoadLogTailAsync(bool older = false)
    {
        var file = SelectedLogFile;
        if (file is null)
        {
            return;
        }

        // Paging older needs an anchor; without one there is nothing before the loaded window.
        if (older && _logOldestOffset is not > 0)
        {
            return;
        }

        await _logReadGate.WaitAsync();
        try
        {
            var args = new List<string>
            {
                file.Name,
                LogTailBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (older)
            {
                args.Add(_logOldestOffset!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

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

            LogTailPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<LogTailPayload>(ack.Message, LogJson);
            }
            catch (JsonException)
            {
                return;
            }

            if (payload is null)
            {
                return;
            }

            if (older)
            {
                _logLines = [.. payload.Lines, .. _logLines];
            }
            else
            {
                _logLines = payload.Lines;
            }

            _logOldestOffset = payload.FirstOffset;
            LogCanPageOlder = payload.Truncated;
            HasLogs = _logLines.Count > 0;
            RebuildLogText();
        }
        finally
        {
            _logReadGate.Release();
        }
    }

    [RelayCommand]
    private async Task LoadOlderLog()
    {
        // Browsing history: stop the heartbeat from snapping back to the live tail and dropping what we page in.
        LogFollow = false;
        await LoadLogTailAsync(older: true);
    }

    partial void OnSelectedLogFileChanged(LogFileChoice? value)
    {
        // A different file (or a reselect) starts from the live tail again.
        _logOldestOffset = null;
        LogCanPageOlder = false;
        if (value is null)
        {
            _logLines = [];
            HasLogs = false;
            RebuildLogText();
            return;
        }

        _ = LoadLogTailAsync();
    }

    partial void OnSearchQueryChanged(string value)
    {
        RebuildLogText();
    }

    partial void OnLogFollowChanged(bool value)
    {
        // Re-enabling follow snaps back to the live tail (dropping any paged-older history).
        if (value)
        {
            _ = LoadLogTailAsync();
        }
    }

    partial void OnLogLevelChanged(string value)
    {
        if (!_suppressSettingPush)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, ["log-level", value]));
        }
    }

    partial void OnRouteLogEnabledChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting,
                ["route-log", value ? "on" : "off"]));
        }
    }

    // Falls an unrecognised token back to the default, so the combo never goes null - which, two-way bound,
    // would push an empty value back.
    private static string KnownLogLevel(string token)
    {
        return token switch
        {
            "trace" or "debug" or "info" => token,
            _ => DefaultLogLevel,
        };
    }

    // OpReadLog ack payload: a bounded window of a log file, oldest line first.
    private sealed record LogTailPayload(
        IReadOnlyList<string> Lines,
        long FirstOffset,
        long FileSize,
        bool Truncated);
}
