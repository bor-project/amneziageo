using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

using Avalonia;
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
    private readonly MainWindowViewModel _host;
    private readonly IAgentConnection _connection;
    private readonly UiPreferences _prefs;

    // Verbosity shown for the agent capture level when the agent reports nothing usable.
    private const string DefaultCaptureLevel = "error";

    // Rows requested per window.
    private const int LogLimit = 400;

    // Destination rows rendered at most; the rest stay behind the filters.
    private const int LiveRowLimit = 1000;

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

    // Ticks of the poll, counted so a source that costs the agent more than a tail read is asked less often.
    private int _tick;

    // The configuration report, as the agent rendered it.
    private string _report = string.Empty;

    // The destination rows the filters left, and how many of them there are over what the agent sent.
    private IReadOnlyList<LiveRowItem> _liveShown = [];
    private string _liveMatches = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public LogsViewModel(MainWindowViewModel host, IAgentConnection connection, UiPreferences prefs)
    {
        _host = host;
        _connection = connection;
        _prefs = prefs;
        // Seed backing field from prefs without echoing OnChanged.
        _probePath = prefs.ProbePath;
        Loc.Instance.CultureChanged += OnCultureChanged;
        BuildWays();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => OnPollTick();
    }

    private void OnCultureChanged()
    {
        if (IsLiveLog)
        {
            BuildWays();
            Render();
        }

        // The probe cards name their tokens in the reader's language: the rows are dropped so they are built
        // again, since the block behind them did not change.
        if (IsProbeLog)
        {
            ProbeEntries.Clear();
            Render();
        }

        OnPropertyChanged(nameof(SearchSummary));
        OnPropertyChanged(nameof(SearchWatermark));
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
        NotifyShape();
    }

    // Width of the panel the viewer sits in, pushed by the view. The pane is narrower than the window around
    // it, and a table that does not fit the pane is unreadable however wide the window is.
    [ObservableProperty]
    private double _paneWidth;

    partial void OnPaneWidthChanged(double value)
    {
        NotifyShape();
    }

    // Pane width the rows stop fitting a table at.
    private const double CardBreakpoint = 560;

    /// <summary>
    /// Whether the rows are carried as cards: the panel is too narrow for a table, whatever the window is.
    /// </summary>
    public bool IsNarrow => PaneWidth > 0 ? PaneWidth < CardBreakpoint : IsCompact;

    // Height of the pane the section is laid out in, pushed by the shell. It is the pane, not the section, so
    // what the layout below does with it cannot come back as a new value.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShort))]
    [NotifyPropertyChangedFor(nameof(SectionHeight))]
    [NotifyPropertyChangedFor(nameof(HeadSpacing))]
    [NotifyPropertyChangedFor(nameof(SectionSpacing))]
    [NotifyPropertyChangedFor(nameof(BarMargin))]
    [NotifyPropertyChangedFor(nameof(ShowControlBarRow))]
    [NotifyPropertyChangedFor(nameof(ShowBarNav))]
    [NotifyPropertyChangedFor(nameof(ShowShortNav))]
    private double _viewportHeight;

    // Height everything but the body takes, measured by the view: the head changes with the source, with the
    // offers under the target field and with the pane width, so it is read rather than counted.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SectionHeight))]
    private double _chromeHeight;

    // Pane height the head stops fitting over a readable body at.
    private const double ShortViewport = 520;

    // Height the body is never laid out below.
    private const double BodyFloor = 180;

    // Gap the section leaves over the body once the head is compact.
    private const double ShortGap = 8;

    // Inset the shell keeps around the section.
    private const double PaneInset = 24;

    /// <summary>
    /// Whether the head is compact: the pane is too low to spend rows on labels and on a bar of its own.
    /// </summary>
    public bool IsShort => ViewportHeight > 0 && ViewportHeight < ShortViewport;

    /// <summary>
    /// Height the section is laid out at: the pane it sits in, or more when the body would otherwise be
    /// squeezed under its floor - the screen around it scrolls for the difference.
    /// </summary>
    public double SectionHeight => ViewportHeight > 0
        ? Math.Max(ViewportHeight - PaneInset, ChromeHeight + BodyFloor + ShortGap)
        : double.NaN;

    /// <summary>
    /// Gap between the rows of the head.
    /// </summary>
    public double HeadSpacing => IsShort ? 6 : 10;

    /// <summary>
    /// Gap between the head and the viewer under it.
    /// </summary>
    public double SectionSpacing => IsShort ? ShortGap : 14;

    /// <summary>
    /// Gap over the control bar.
    /// </summary>
    public Thickness BarMargin => IsShort ? new Thickness(0, 4, 0, 0) : new Thickness(0, 10, 0, 0);

    /// <summary>
    /// Whether the control bar takes a row of its own: a low pane carries its controls in the search row and
    /// keeps the row for the frozen hint alone.
    /// </summary>
    public bool ShowControlBarRow => IsFrozen || (ShowControlBar && !IsShort);

    /// <summary>
    /// Whether paging and following stand in the control bar.
    /// </summary>
    public bool ShowBarNav => IsStoredLog && !IsShort;

    /// <summary>
    /// Whether paging and following stand in the search row, which is where a low pane carries them.
    /// </summary>
    public bool ShowShortNav => IsStoredLog && IsShort;

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(IsNarrow));
        OnPropertyChanged(nameof(BareBody));
        OnPropertyChanged(nameof(ShowStoredText));
        OnPropertyChanged(nameof(ShowTableText));
        OnPropertyChanged(nameof(ShowStoredCards));
        OnPropertyChanged(nameof(ShowProbeCards));
        OnPropertyChanged(nameof(ShowLiveCards));
        OnPropertyChanged(nameof(SearchWatermark));
        Render();
    }

    // --- Log type ---

    /// <summary>
    /// The live source: what the tunnel carries right now, asked of the agent instead of a stored table.
    /// </summary>
    public const string LiveType = "active";

    /// <summary>
    /// The probe journal: what measuring one destination left behind.
    /// </summary>
    public const string ProbeType = "probe";

    /// <summary>
    /// The configuration the agent runs on, asked of it instead of read from a table.
    /// </summary>
    public const string ConfigType = "config";

    /// <summary>
    /// The selectable sources. The tokens are the same in every language.
    /// </summary>
    public ObservableCollection<string> LogTypes { get; } = ["ageo", "routes", LiveType, ConfigType];

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
    /// Whether the viewer is on the probe journal, which carries the block that fills it.
    /// </summary>
    public bool IsProbeLog => SelectedLogType == ProbeType;

    /// <summary>
    /// Whether the viewer is on what the tunnel carries right now.
    /// </summary>
    public bool IsLiveLog => SelectedLogType == LiveType;

    /// <summary>
    /// Whether the viewer is on the configuration the agent runs on.
    /// </summary>
    public bool IsConfigLog => SelectedLogType == ConfigType;

    /// <summary>
    /// Whether the viewer is on a source the agent answers out of what it holds right now: nothing is recorded
    /// behind it, so there is no history to page through and nothing to clear.
    /// </summary>
    public bool IsRuntimeLog => IsLiveLog || IsConfigLog;

    /// <summary>
    /// Whether the viewer is on a stored table, which is what can be searched, paged and cleared.
    /// </summary>
    public bool IsStoredLog => !IsRuntimeLog;

    /// <summary>
    /// Whether the search field is shown: it searches a stored table, and narrows the destinations where they lie.
    /// </summary>
    public bool ShowSearch => IsStoredLog || IsLiveLog;

    partial void OnSelectedLogTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAgentLog));
        OnPropertyChanged(nameof(IsRouteLog));
        OnPropertyChanged(nameof(IsProbeLog));
        OnPropertyChanged(nameof(IsLiveLog));
        OnPropertyChanged(nameof(IsConfigLog));
        OnPropertyChanged(nameof(IsRuntimeLog));
        OnPropertyChanged(nameof(IsStoredLog));
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(SearchSummary));
        OnPropertyChanged(nameof(ShowControlBar));
        OnPropertyChanged(nameof(ShowControlBarRow));
        OnPropertyChanged(nameof(ShowBarNav));
        OnPropertyChanged(nameof(ShowShortNav));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(BareBody));
        OnPropertyChanged(nameof(ShowStoredText));
        OnPropertyChanged(nameof(ShowTableText));
        OnPropertyChanged(nameof(ShowStoredCards));
        OnPropertyChanged(nameof(ShowProbeCards));
        OnPropertyChanged(nameof(ShowLiveCards));
        ClearView();
        ResetAndReload();
        if (IsProbeLog)
        {
            _knownFor = (TunnelUp, string.Empty);
            _ = LoadKnownAsync();
        }
    }

    // --- Target probe (probe) ---

    /// <summary>
    /// The shell, for the server the probe is measured through: the picker there is the one the home screen
    /// carries, so a change here moves a live tunnel exactly as it does there.
    /// </summary>
    public MainWindowViewModel Shell => _host;

    /// <summary>
    /// The destination a probe measures.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunProbeCommand))]
    private string _probeTarget = string.Empty;

    partial void OnProbeTargetChanged(string value)
    {
        RefreshSuggestions();
    }

    /// <summary>
    /// Path a probe is measured over: auto, tunnel or bypass. Kept across launches.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunProbeCommand))]
    private string _probePath = ProbePaths.Auto;

    partial void OnProbePathChanged(string value)
    {
        OnPropertyChanged(nameof(ProbePathIndex));
        OnPropertyChanged(nameof(IsProbeBypass));
        OnPropertyChanged(nameof(ServerOpacity));
        OnPropertyChanged(nameof(PathNeedsTunnel));
        OnPropertyChanged(nameof(ProbeFormEnabled));
        _prefs.ProbePath = value;
        _prefs.Save();
    }

    /// <summary>
    /// The path as the picker holds it: auto, tunnel, bypass.
    /// </summary>
    public int ProbePathIndex
    {
        get => ProbePath switch
        {
            ProbePaths.Tunnel => 1,
            ProbePaths.Bypass => 2,
            _ => 0,
        };
        set => ProbePath = value switch
        {
            1 => ProbePaths.Tunnel,
            2 => ProbePaths.Bypass,
            _ => ProbePaths.Auto,
        };
    }

    /// <summary>
    /// Whether the destination is held past the tunnel for the run.
    /// </summary>
    public bool IsProbeBypass => ProbePath == ProbePaths.Bypass;

    // Whether a tunnel is up. Without one only the way past it can be measured, and the run is not allowed to
    // raise one: a probe measures what is there, it does not change it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathNeedsTunnel))]
    [NotifyPropertyChangedFor(nameof(ProbeFormEnabled))]
    [NotifyCanExecuteChangedFor(nameof(RunProbeCommand))]
    private bool _tunnelUp;

    // Whether a server is picked to measure through.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunProbeCommand))]
    private bool _serverPicked;

    /// <summary>
    /// Whether the picked path asks for a tunnel that is not up: the pick stands, the run behind it does not.
    /// </summary>
    public bool PathNeedsTunnel => !TunnelUp && !IsProbeBypass;

    /// <summary>
    /// Whether the destination and the run behind it are open to the finger.
    /// </summary>
    public bool ProbeFormEnabled => !PathNeedsTunnel;

    /// <summary>
    /// How strongly the server picker is drawn: a run past the tunnel does not go through the server at all.
    /// </summary>
    public double ServerOpacity => IsProbeBypass ? 0.45 : 1;

    /// <summary>
    /// Destinations offered under the target field: the names the tunnel resolved and the hosts it carries.
    /// </summary>
    public ObservableCollection<string> ProbeSuggestions { get; } = [];

    /// <summary>
    /// Whether anything is offered under the target field.
    /// </summary>
    public bool ShowProbeSuggestions => ProbeSuggestions.Count > 0;

    // Everything the agent knows a name for, filtered as the field is typed in.
    private IReadOnlyList<string> _known = [];

    // The tunnel state and the server the offered names were read for.
    private (bool Up, string Server) _knownFor = (false, string.Empty);

    // Suggestions offered at once; a longer list would push the journal off the pane.
    private const int MaxSuggestions = 6;

    /// <summary>
    /// Takes an offered destination into the field.
    /// </summary>
    public void PickSuggestion(string value)
    {
        ProbeTarget = value;
        ProbeSuggestions.Clear();
        OnPropertyChanged(nameof(ShowProbeSuggestions));
    }

    // Ссылка «Замер» рядом с запуском: сервис, на котором меряется скорость, живёт своим экраном.
    [RelayCommand]
    private void OpenProbeSettings()
    {
        _host.ShowProbeSettings();
    }

    // Whether a probe is in flight.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunProbeCommand))]
    private bool _probeRunning;

    [RelayCommand(CanExecute = nameof(CanRunProbe))]
    private async Task RunProbe()
    {
        var target = ProbeTarget.Trim();
        if (target.Length == 0)
        {
            return;
        }

        ProbeSuggestions.Clear();
        OnPropertyChanged(nameof(ShowProbeSuggestions));
        ProbeRunning = true;
        try
        {
            await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpProbeTarget,
                [target, ProbePath, _host.Probe.UploadUrl.Trim()]));
        }
        catch
        {
            return;
        }
        finally
        {
            ProbeRunning = false;
        }

        ResetAndReload();
    }

    private bool CanRunProbe => !ProbeRunning && ServerPicked && !PathNeedsTunnel && ProbeTarget.Trim().Length > 0;

    // The probe journal is offered only where there is a server to measure through.
    private void SyncProbeSource(bool hasServers)
    {
        if (hasServers == LogTypes.Contains(ProbeType))
        {
            return;
        }

        if (hasServers)
        {
            // Beside the other journals: the sources that are only ever asked for close the list.
            LogTypes.Insert(LogTypes.IndexOf(ConfigType), ProbeType);
            return;
        }

        if (IsProbeLog)
        {
            SelectedLogType = "ageo";
        }

        LogTypes.Remove(ProbeType);
    }

    // What the agent has a name for. Read on opening the journal and again whenever the tunnel or the server
    // behind the names changes, because a run past the tunnel is measured exactly when neither is up.
    private async Task LoadKnownAsync()
    {
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpKnownHosts, []));
            if (!ack.Ok)
            {
                return;
            }

            _known = [.. ack.Message
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        }
        catch
        {
            return;
        }

        RefreshSuggestions();
    }

    // Offers what the typed text stands for; an empty field offers nothing, and neither does an exact hit.
    private void RefreshSuggestions()
    {
        var typed = ProbeTarget.Trim();
        ProbeSuggestions.Clear();
        if (typed.Length > 0)
        {
            foreach (var name in _known)
            {
                if (ProbeSuggestions.Count >= MaxSuggestions)
                {
                    break;
                }

                if (name.Contains(typed, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, typed, StringComparison.OrdinalIgnoreCase))
                {
                    ProbeSuggestions.Add(name);
                }
            }
        }

        OnPropertyChanged(nameof(ShowProbeSuggestions));
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

        // The level the agent records at is the floor the viewer shows, so the rows follow the setting.
        if (IsAgentLog && IsActive)
        {
            ResetAndReload();
        }
    }

    // --- Way (active): where a row goes, or every row ---

    /// <summary>
    /// The paths the destinations can be narrowed to, named in the reader's language.
    /// </summary>
    public ObservableCollection<WayChoice> WayChoices { get; } = [];

    [ObservableProperty]
    private WayChoice? _selectedWay;

    partial void OnSelectedWayChanged(WayChoice? value)
    {
        if (IsLiveLog)
        {
            Render();
        }
    }

    // Fills the path filter in the reader's language, keeping whatever it was set to.
    private void BuildWays()
    {
        var token = SelectedWay?.Token ?? WayChoice.Any;
        WayChoices.Clear();
        WayChoices.Add(new WayChoice(WayChoice.Any, Loc.Instance.Get("Main_WayAll")));
        WayChoices.Add(new WayChoice(LiveSession.PathTunnel, Loc.Instance.Get("Check_Verdict_proxy")));
        WayChoices.Add(new WayChoice(LiveSession.PathDirect, Loc.Instance.Get("Check_Verdict_direct")));
        WayChoices.Add(new WayChoice(LiveSession.PathBlock, Loc.Instance.Get("Check_Verdict_block")));
        SelectedWay = WayChoices.FirstOrDefault(way => way.Token == token) ?? WayChoices[0];
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
        if (IsLiveLog)
        {
            // The destinations are already held; narrowing them asks the agent for nothing.
            Render();
            return;
        }

        // Search updates as you type; no loader (it would flash the body on every keystroke).
        ResetAndReload(showLoader: false);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private int _searchMatchCount;

    /// <summary>
    /// The hint in the search field. A narrow pane leaves it no room for what is searched through, so there it
    /// names the verb alone.
    /// </summary>
    public string SearchWatermark =>
        Loc.Instance.Get(IsNarrow ? "Main_LogSearchWatermarkShort" : "Main_LogSearchWatermark");

    /// <summary>
    /// What the field beside it came to: the matches in a stored table, the destinations left of the report.
    /// </summary>
    public string SearchSummary
    {
        get
        {
            if (IsLiveLog)
            {
                return _liveMatches;
            }

            return string.IsNullOrWhiteSpace(SearchQuery)
                ? string.Empty
                : Loc.Instance.Get("MainVm_LogSearchMatches", SearchMatchCount);
        }
    }

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
    /// Probes as cards, which is what a narrow window shows instead of the block they are rendered as.
    /// </summary>
    public ObservableCollection<ProbeEntryItem> ProbeEntries { get; } = [];

    /// <summary>
    /// Destinations as cards, which is what a narrow window shows instead of the text.
    /// </summary>
    public ObservableCollection<LiveRowItem> LiveRows { get; } = [];

    // What the tunnel carries in one line, above the rows it counts.
    [ObservableProperty]
    private string _liveSummary = string.Empty;

    // Held Ctrl: the viewer stops taking new rows, so a selection survives long enough to be copied.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowControlBar))]
    [NotifyPropertyChangedFor(nameof(ShowControlBarRow))]
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
    [NotifyPropertyChangedFor(nameof(ShowTableText))]
    [NotifyPropertyChangedFor(nameof(ShowStoredCards))]
    [NotifyPropertyChangedFor(nameof(ShowProbeCards))]
    [NotifyPropertyChangedFor(nameof(ShowLiveCards))]
    private bool _hasLogs;

    // Whether a window load is in flight; shows the loader in place of the body (not raised for the tail poll).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowStoredText))]
    [NotifyPropertyChangedFor(nameof(ShowTableText))]
    [NotifyPropertyChangedFor(nameof(ShowStoredCards))]
    [NotifyPropertyChangedFor(nameof(ShowProbeCards))]
    [NotifyPropertyChangedFor(nameof(ShowLiveCards))]
    private bool _isLoading;

    /// <summary>
    /// Whether the log body is shown: there is content and no load is in flight.
    /// </summary>
    public bool ShowBody => HasLogs && !IsLoading;

    /// <summary>
    /// Whether the stored rows are shown as text, which is what a wide window carries.
    /// </summary>
    public bool ShowStoredText => ShowBody && !IsNarrow && IsStoredLog;

    /// <summary>
    /// Whether the body frame is dropped: the cards carry one of their own, so it would only take width off
    /// them. The report text has none, and keeps the frame at every width.
    /// </summary>
    public bool BareBody => IsNarrow && !IsConfigLog;

    /// <summary>
    /// Whether the body is the padded table: the destinations at a width that fits them, and the configuration
    /// report at any width, because it does not read as a column of cards.
    /// </summary>
    public bool ShowTableText => ShowBody && (IsConfigLog || (IsLiveLog && !IsNarrow));

    /// <summary>
    /// Whether the stored rows are shown as cards, which is what a narrow window carries. The probes are cards
    /// of their own, laid out from what their block holds.
    /// </summary>
    public bool ShowStoredCards => ShowBody && IsNarrow && IsStoredLog && !IsProbeLog;

    /// <summary>
    /// Whether the probes are shown as cards, which is what a narrow window carries.
    /// </summary>
    public bool ShowProbeCards => ShowBody && IsNarrow && IsProbeLog;

    /// <summary>
    /// Whether the destinations are shown as cards, which is what a narrow window carries.
    /// </summary>
    public bool ShowLiveCards => ShowBody && IsNarrow && IsLiveLog;

    /// <summary>
    /// Whether the empty hint is shown: no content and no load in flight. The live source says the same thing
    /// in its summary line, so it does not carry the hint as well.
    /// </summary>
    public bool ShowEmpty => !HasLogs && !IsLoading && !IsLiveLog;

    /// <summary>
    /// Whether the control bar is shown: the live source has nothing to page and follows always, so it carries
    /// the bar only while the viewer is frozen.
    /// </summary>
    public bool ShowControlBar => IsStoredLog || IsFrozen;

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
        TunnelUp = snapshot.BoundStatus == ConnectionStatus.Connected;
        ServerPicked = !string.IsNullOrEmpty(snapshot.SelectedTarget);
        SyncProbeSource(snapshot.Configs.Count > 0);
        var picked = snapshot.SelectedTarget ?? string.Empty;
        if (IsProbeLog && _knownFor != (TunnelUp, picked))
        {
            _knownFor = (TunnelUp, picked);
            _ = LoadKnownAsync();
        }
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
        _report = string.Empty;
        _liveShown = [];
        _liveMatches = string.Empty;
        LogText = string.Empty;
        LiveSummary = string.Empty;
        ClearCards();
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

        // The configuration and the destinations cost the agent a round of reads to answer and move far more
        // slowly than a tail does, so they are re-read every other tick.
        _tick++;
        if (IsRuntimeLog && _tick % 2 != 0)
        {
            return;
        }

        // A runtime source has no history to walk, so a tick always re-reads it.
        if (IsRuntimeLog || (_cursor is null && _cursorStack.Count == 0))
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
        if (IsRuntimeLog)
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
    /// What the viewer shows right now, as text for the clipboard.
    /// </summary>
    public string VisibleText()
    {
        if (IsLiveLog)
        {
            return LiveSummary + "\n\n" + SessionRows.Text(_liveShown);
        }

        return LogText.Length > 0 ? LogText : string.Join('\n', _lines);
    }

    /// <summary>
    /// Renders the whole selected table through the agent; null when the agent did not answer.
    /// </summary>
    public async Task<string?> BuildExportTextAsync()
    {
        if (IsRuntimeLog)
        {
            // Nothing is stored behind a runtime source: what travels is what is on screen.
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

        if (IsConfigLog)
        {
            await LoadRuntimeConfigAsync();
            return;
        }

        var type = SelectedLogType;
        var args = new List<string>
        {
            type,
            LogLimit.ToString(CultureInfo.InvariantCulture),
            (beforeId ?? 0).ToString(CultureInfo.InvariantCulture),
            type == "ageo" ? CaptureLevel : string.Empty,
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
        SearchMatchCount = 0;
        LogCanPageOlder = false;
        LogCanPageNewer = false;
        Render();
    }

    // Reads the configuration the agent runs on, or would run on at the next connect.
    private async Task LoadRuntimeConfigAsync()
    {
        IpcAck ack;
        try
        {
            ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetRuntimeConfig, []));
        }
        catch
        {
            return;
        }

        // Source left during the pipe round-trip: drop the reply so the freed state stays freed.
        if (!IsActive || !IsConfigLog)
        {
            return;
        }

        _report = ack.Ok ? ack.Message : Describe(ack);
        HasLogs = _report.Length > 0;
        SearchMatchCount = 0;
        LogCanPageOlder = false;
        LogCanPageNewer = false;
        Render();
    }

    // Resolves a failed ack to text: the agent sends localization keys, not sentences.
    private static string Describe(IpcAck ack)
    {
        return IpcMessage.TryParse(ack.Message, out var key, out var args)
            ? Loc.Instance.Get(key, args)
            : ack.Message;
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
        if (IsConfigLog)
        {
            ClearCards();
            LogText = _report;
            return;
        }

        if (!IsNarrow)
        {
            ClearCards();
            LogText = _lines.Count > 0 ? string.Join('\n', _lines) : string.Empty;
            return;
        }

        LogText = string.Empty;
        if (IsProbeLog)
        {
            Entries.Clear();
            Fill(ProbeEntries, Probes());
            return;
        }

        ProbeEntries.Clear();
        var rows = new List<LogEntryItem>(_lines.Count);
        foreach (var line in _lines)
        {
            rows.Add(LogEntryItem.Parse(line));
        }

        Fill(Entries, rows);
    }

    // The window as probe cards: one card per rendered probe.
    private IReadOnlyList<ProbeEntryItem> Probes()
    {
        var rows = new List<ProbeEntryItem>(_lines.Count);
        foreach (var line in _lines)
        {
            rows.Add(ProbeEntryItem.Parse(line));
        }

        return rows;
    }

    // Drops both card lists: a source fills one of them.
    private void ClearCards()
    {
        Entries.Clear();
        ProbeEntries.Clear();
    }

    // Puts the destinations on screen through the path and the text over them.
    private void RenderCarried()
    {
        ClearCards();
        LiveSummary = SessionRows.Summary(_carried);
        var needle = SearchQuery?.Trim() ?? string.Empty;
        var way = SelectedWay?.Token ?? WayChoice.Any;
        var rows = new List<LiveRowItem>();
        var matched = 0;
        foreach (var row in SessionRows.Cards(_carried))
        {
            if (way != WayChoice.Any && !string.Equals(row.Way, way, StringComparison.Ordinal))
            {
                continue;
            }

            if (needle.Length > 0
                && !row.Host.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched++;
            if (rows.Count < LiveRowLimit)
            {
                rows.Add(row);
            }
        }

        _liveShown = rows;
        _liveMatches = _carried.Held > _carried.Sessions.Count
            ? Loc.Instance.Get("MainVm_LiveShownCapped", rows.Count, matched, _carried.Held)
            : Loc.Instance.Get("MainVm_LiveShown", rows.Count, matched);
        OnPropertyChanged(nameof(SearchSummary));
        HasLogs = rows.Count > 0;
        if (!IsNarrow)
        {
            LiveRows.Clear();
            LogText = SessionRows.Text(rows);
            return;
        }

        LogText = string.Empty;
        Fill(LiveRows, rows);
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

/// <summary>
/// One path the destinations can be narrowed to: the token it filters by and its name in the reader's language.
/// </summary>
internal sealed record WayChoice(string Token, string Name)
{
    /// <summary>
    /// Token that narrows nothing.
    /// </summary>
    public const string Any = "all";
}
