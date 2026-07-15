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
    private const string DefaultLogLevel = "warning";

    // Tail window requested per read (bytes); the agent clamps it.
    private const int LogTailBytes = 262144;

    private static readonly JsonSerializerOptions LogJson = new() { PropertyNameCaseInsensitive = true };

    private bool _suppressSettingPush;
    private IReadOnlyList<string> _logLines = [];

    // Serializes log reads so overlapping heartbeat/user loads never interleave on the pipe.
    private readonly System.Threading.SemaphoreSlim _logReadGate = new(1, 1);

    // Current shown window in byte space: [_windowFirst, _windowEnd) of a file of _fileSize bytes.
    // _windowEnd == _fileSize means the window sits on the live tail.
    private long _windowFirst;
    private long _windowEnd;
    private long _fileSize;

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
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged()
    {
        OnPropertyChanged(nameof(SearchSummary));
    }

    /// <summary>
    /// Whether the logs section is the one currently shown; gates the file-backed viewer's heartbeat re-reads.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Log verbosity options. The tokens are the same in every language.
    /// </summary>
    public ObservableCollection<string> LogLevels { get; } = ["error", "warning", "info", "debug", "trace"];

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

    // Older content exists before the window (enables back paging).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PageOlderCommand))]
    private bool _logCanPageOlder;

    // Newer content exists after the window (enables forward paging).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PageNewerCommand))]
    private bool _logCanPageNewer;

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
        _windowFirst = 0;
        _windowEnd = 0;
        _fileSize = 0;
        LogCanPageOlder = false;
        LogCanPageNewer = false;
        LogFiles.Clear();
        SelectedLogFile = null;
        _logLines = [];
        HasLogs = false;
        RebuildLogText();
    }

    // Ceiling on lines handed to the unvirtualized text control.
    private const int MaxShownLines = 400;

    // Rebuilds the journal text from the raw lines applying the level filter and the search query, newest
    // first so the latest activity stays visible at the top without scrolling.
    private void RebuildLogText()
    {
        var query = SearchQuery;
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var threshold = SelectedThreshold(LogLevel);

        var shown = new List<string>();
        foreach (var line in _logLines)
        {
            var severity = LineSeverity(line);
            if (severity >= 0 && severity < threshold)
            {
                continue;
            }

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
        if (shown.Count > MaxShownLines)
        {
            shown.RemoveRange(MaxShownLines, shown.Count - MaxShownLines);
        }

        LogText = string.Join('\n', shown);
    }

    // Minimum severity shown for the picked verbosity level; anything below is hidden in the viewer.
    private static int SelectedThreshold(string level)
    {
        return level switch
        {
            "trace" => 0,
            "debug" => 1,
            "info" => 2,
            "warning" => 3,
            _ => 4,
        };
    }

    // Severity parsed from a line's bracketed level token; -1 when a line carries none (wrapped exception
    // continuations) so it is never hidden by the level filter. Route-log lines carry their own [INF]/[ERR]
    // token, so the picker filters them by the selected level and above, same as the agent log.
    private static int LineSeverity(string line)
    {
        if (line.Contains("[ERR]", StringComparison.Ordinal) || line.Contains("[FTL]", StringComparison.Ordinal))
        {
            return 4;
        }

        if (line.Contains("[WRN]", StringComparison.Ordinal))
        {
            return 3;
        }

        if (line.Contains("[INF]", StringComparison.Ordinal))
        {
            return 2;
        }

        if (line.Contains("[DBG]", StringComparison.Ordinal))
        {
            return 1;
        }

        if (line.Contains("[VRB]", StringComparison.Ordinal) || line.Contains("[TRC]", StringComparison.Ordinal))
        {
            return 0;
        }

        return -1;
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
            _ = LoadWindowAsync(null);
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

            // Show only the canonical logs by name; the size-roll generations (routes.log.1..5) are hidden
            // so the picker offers one entry per log, not a pile of rotated files.
            metas = metas?.Where(m => !m.Name.Contains(".log.", StringComparison.Ordinal)).ToList();
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
                await LoadWindowAsync(null);
            }
        }
        finally
        {
            _logListBusy = false;
        }
    }

    // Reads a byte window of the selected log file over IPC and makes it the shown page: the live tail when
    // endOffset is null, otherwise the window ending at endOffset. Serialized so heartbeat and user loads
    // never interleave on the pipe.
    private async Task LoadWindowAsync(long? endOffset)
    {
        var file = SelectedLogFile;
        if (file is null)
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
            if (endOffset is > 0)
            {
                args.Add(endOffset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

            _logLines = payload.Lines;
            _windowFirst = payload.FirstOffset;
            _fileSize = payload.FileSize;
            _windowEnd = endOffset ?? payload.FileSize;
            LogCanPageOlder = payload.Truncated;
            LogCanPageNewer = _windowEnd < payload.FileSize;
            HasLogs = _logLines.Count > 0;
            RebuildLogText();
        }
        finally
        {
            _logReadGate.Release();
        }
    }

    [RelayCommand]
    private async Task ClearLog()
    {
        try
        {
            await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpClearLog, []));
        }
        catch
        {
            return;
        }

        // Re-list and re-read so the freshly emptied file replaces the shown tail.
        await RefreshLogFilesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanPageOlder))]
    private async Task PageOlder()
    {
        // Browsing history: stop the heartbeat from snapping back to the live tail and dropping the page.
        LogFollow = false;
        await LoadWindowAsync(_windowFirst);
    }

    [RelayCommand(CanExecute = nameof(CanPageNewer))]
    private async Task PageNewer()
    {
        var newEnd = _windowEnd + LogTailBytes;
        if (newEnd >= _fileSize)
        {
            // Reached the newest page: snap to the live tail (OnLogFollowChanged reloads it).
            LogFollow = true;
            return;
        }

        LogFollow = false;
        await LoadWindowAsync(newEnd);
    }

    private bool CanPageOlder => LogCanPageOlder;

    private bool CanPageNewer => LogCanPageNewer;

    partial void OnSelectedLogFileChanged(LogFileChoice? value)
    {
        if (value is null)
        {
            _windowFirst = 0;
            _windowEnd = 0;
            _fileSize = 0;
            LogCanPageOlder = false;
            LogCanPageNewer = false;
            _logLines = [];
            HasLogs = false;
            RebuildLogText();
            return;
        }

        // A different file (or a reselect) starts from the live tail again.
        _ = LoadWindowAsync(null);
    }

    partial void OnSearchQueryChanged(string value)
    {
        RebuildLogText();
    }

    partial void OnLogFollowChanged(bool value)
    {
        // Re-enabling follow snaps back to the live tail (dropping any paged history).
        if (value)
        {
            _ = LoadWindowAsync(null);
        }
    }

    partial void OnLogLevelChanged(string value)
    {
        // The level is both the capture verbosity (pushed to the agent) and the viewer's filter threshold.
        RebuildLogText();
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
            "trace" or "debug" or "info" or "warning" or "error" => token,
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
