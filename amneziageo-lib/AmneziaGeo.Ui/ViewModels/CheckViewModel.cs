using System.Text;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Diagnostics check pane: runs the channel ladder or asks about one destination, and shows what was measured
/// with the phrase that names the culprit. Every run is stored by the agent, so what is on screen is also what
/// travels in the support archive.
/// </summary>
internal sealed partial class CheckViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;

    /// <summary>
    /// ctor
    /// </summary>
    public CheckViewModel(IAgentConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Whether the pane is the one currently shown.
    /// </summary>
    public bool IsActive { get; private set; }

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    private bool _isCompact;

    // What a targeted check asks about: a domain, an address, an app token or a geo rule.
    [ObservableProperty]
    private string _target = string.Empty;

    // Whether a run is in flight; the ladder takes about twenty seconds.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isRunning;

    // The phrase that names the culprit.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVerdict))]
    private string _verdict = string.Empty;

    // Whether the verdict names something to fix, which colours the banner.
    [ObservableProperty]
    private bool _blamed;

    // The MTU to set, when the one in force does not fit the measured path.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdvice))]
    private string _advice = string.Empty;

    // What was measured, one row per leg or per fact.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string _bodyText = string.Empty;

    /// <summary>
    /// Whether the measured rows are shown.
    /// </summary>
    public bool ShowBody => !IsRunning && BodyText.Length > 0;

    /// <summary>
    /// Whether the empty hint is shown.
    /// </summary>
    public bool ShowEmpty => !IsRunning && BodyText.Length == 0;

    /// <summary>
    /// Whether a verdict is shown.
    /// </summary>
    public bool ShowVerdict => Verdict.Length > 0;

    /// <summary>
    /// Whether the MTU advice is shown.
    /// </summary>
    public bool ShowAdvice => Advice.Length > 0;

    /// <summary>
    /// Marks the pane shown or not; leaving it drops what was read.
    /// </summary>
    public void SetActive(bool active)
    {
        if (active == IsActive)
        {
            return;
        }

        IsActive = active;
        if (!active)
        {
            BodyText = string.Empty;
            Verdict = string.Empty;
            Advice = string.Empty;
            Blamed = false;
        }
    }

    [RelayCommand]
    private async Task RunChannelAsync()
    {
        await RunAsync(new IpcCommand(IpcContract.OpCheckChannel, []), RunKind.Channel);
    }

    [RelayCommand]
    private async Task RunServersAsync()
    {
        await RunAsync(new IpcCommand(IpcContract.OpCheckServers, []), RunKind.Servers);
    }

    [RelayCommand]
    private async Task RunSessionsAsync()
    {
        await RunAsync(new IpcCommand(IpcContract.OpGetSessions, []), RunKind.Sessions);
    }

    [RelayCommand]
    private async Task RunTargetAsync()
    {
        var target = Target.Trim();
        if (target.Length == 0)
        {
            return;
        }

        await RunAsync(new IpcCommand(IpcContract.OpCheckTarget, [target]), RunKind.Target);
    }

    private async Task RunAsync(IpcCommand command, RunKind kind)
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        BodyText = string.Empty;
        Verdict = string.Empty;
        Advice = string.Empty;
        IpcAck ack;
        try
        {
            ack = await _connection.SendCommandAsync(command);
        }
        catch
        {
            IsRunning = false;
            return;
        }

        IsRunning = false;
        if (!IsActive)
        {
            return;
        }

        if (!ack.Ok)
        {
            Blamed = true;
            Verdict = Describe(ack);
            return;
        }

        Show(ack.Message, kind);
    }

    // Fills the pane from the ack: the rows as measured, then the verdict in words.
    private void Show(string payload, RunKind kind)
    {
        if (kind == RunKind.Channel)
        {
            var report = CheckReport.Parse(payload);
            BodyText = Legs(report);
            Blamed = report.Culprit.Length > 0;
            Advice = report.Advice is { } advice ? Loc.Instance.Get("Check_MtuAdvice", [.. advice.Args()]) : string.Empty;
            Verdict = Loc.Instance.Get(report.VerdictKey, [.. report.VerdictArgs]);
            return;
        }

        if (kind == RunKind.Servers)
        {
            var sweep = SweepReport.Parse(payload);
            BodyText = Rows(sweep);
            Blamed = sweep.VerdictKey != CheckVerdicts.SweepBest;
            Verdict = Loc.Instance.Get(sweep.VerdictKey, [.. sweep.VerdictArgs]);
            return;
        }

        if (kind == RunKind.Sessions)
        {
            var carried = SessionReport.Parse(payload);
            BodyText = Destinations(carried);
            Blamed = carried.Stalled > 0;
            Verdict = carried.Held == 0
                ? Loc.Instance.Get("Check_SessionsNone")
                : Loc.Instance.Get("Check_SessionsSummary", carried.Held, carried.Undecided, carried.Stalled,
                    CheckFormat.Bytes(carried.TotalBytes));
            return;
        }

        var target = TargetReport.Parse(payload);
        BodyText = Facts(target);
        Blamed = target.VerdictKey is not (TargetVerdicts.Proxy or TargetVerdicts.UnlistedFull);
        Verdict = Loc.Instance.Get(target.VerdictKey, [.. target.VerdictArgs]);
    }

    // The sweep: the gateway everything is measured through, then one line per server.
    private static string Rows(SweepReport report)
    {
        var text = new StringBuilder();
        if (report.Gateway is { } gateway)
        {
            text.Append("  ")
                .Append(Loc.Instance.Get("Check_Leg_gateway").PadRight(20))
                .Append(Loc.Instance.Get($"Check_State_{gateway.State}").PadRight(16))
                .Append(Measured(gateway))
                .Append('\n');
        }

        foreach (var row in report.Servers)
        {
            text.Append(row.Best ? "* " : "  ")
                .Append(row.Config.PadRight(20))
                .Append(Loc.Instance.Get($"Check_State_{row.State}").PadRight(16))
                .Append(Measured(new CheckLeg(row.Config, row.State, row.RttMs, row.JitterMs, row.LossPercent, Note: row.Note)))
                .Append(row.Live ? $" \u00b7 {Loc.Instance.Get("Check_SweepLive")}" : string.Empty)
                .Append('\n');
        }

        return text.ToString();
    }

    // What the tunnel carries, busiest first: where each destination goes and what it holds.
    private static string Destinations(SessionReport report)
    {
        var text = new StringBuilder();
        foreach (var row in report.Sessions)
        {
            text.Append(row.Host.PadRight(34))
                .Append(Word(row.Verdict).PadRight(16))
                .Append(Carried(row))
                .Append('\n');
        }

        return text.ToString();
    }

    // What one destination holds: how much has gone there, how fast it moves now, and how long it has been quiet.
    private static string Carried(LiveSession row)
    {
        var parts = new List<string>();
        if (row.App.Length > 0)
        {
            parts.Add(row.App);
        }

        if (row.Bytes > 0)
        {
            parts.Add(CheckFormat.Bytes(row.Bytes));
        }

        if (row.BitsPerSecond >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_Rate", CheckFormat.Mbits(row.BitsPerSecond)));
        }

        if (row.Live > 0)
        {
            parts.Add(Loc.Instance.Get("Check_SessionLive", row.Live));
        }

        if (row.AgeSeconds >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_SessionAge", row.AgeSeconds));
        }

        if (row.IdleSeconds >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_SessionIdle", row.IdleSeconds));
        }

        if (row.Stalled)
        {
            parts.Add(Loc.Instance.Get("Check_SessionStalled"));
        }

        return parts.Count == 0 ? "-" : string.Join(" \u00b7 ", parts);
    }

    // The verdict in the window's language; an agent that says something else says it in its own words.
    private static string Word(string verdict)
    {
        return verdict is "proxy" or "direct" or "block" or LiveSession.Undecided
            ? Loc.Instance.Get($"Check_Verdict_{verdict}")
            : verdict;
    }

    private static string Legs(CheckReport report)
    {
        var text = new StringBuilder();
        foreach (var leg in report.Legs)
        {
            text.Append(Loc.Instance.Get($"Check_Leg_{leg.Name}").PadRight(22))
                .Append(Loc.Instance.Get($"Check_State_{leg.State}").PadRight(16))
                .Append(Measured(leg))
                .Append('\n');
        }

        return text.ToString();
    }

    // What one leg measured, in the units of the window.
    private static string Measured(CheckLeg leg)
    {
        var parts = new List<string>();
        if (leg.RttMs >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_Rtt", leg.RttMs));
        }

        if (leg.JitterMs > 0)
        {
            parts.Add(Loc.Instance.Get("Check_Jitter", leg.JitterMs));
        }

        if (LinkHealth.LossKnown(leg.LossPercent))
        {
            parts.Add(Loc.Instance.Get("Check_Loss", leg.LossPercent));
        }

        if (leg.MaxPacketBytes > 0)
        {
            parts.Add(Loc.Instance.Get("Check_Size", leg.MaxPacketBytes));
        }

        if (leg.AgeSeconds >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_Age", leg.AgeSeconds));
        }

        if (leg.RekeysPerMinute >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_Rekeys", leg.RekeysPerMinute));
        }

        if (leg.BitsPerSecond >= 0)
        {
            parts.Add(Loc.Instance.Get("Check_Rate", CheckFormat.Mbits(leg.BitsPerSecond)));
        }

        if (leg.Note.Length > 0)
        {
            parts.Add(leg.Note);
        }

        return parts.Count == 0 ? "-" : string.Join(" · ", parts);
    }

    private static string Facts(TargetReport report)
    {
        var text = new StringBuilder();
        foreach (var fact in report.Facts)
        {
            text.Append(fact.Kind.PadRight(12))
                .Append(fact.Name.PadRight(30))
                .Append(fact.State.PadRight(12))
                .Append(fact.Detail)
                .Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// What one run of the pane asks the agent for.
    /// </summary>
    private enum RunKind
    {
        /// <summary>
        /// The ladder for the config in force.
        /// </summary>
        Channel,

        /// <summary>
        /// Every saved server, light legs only.
        /// </summary>
        Servers,

        /// <summary>
        /// One destination.
        /// </summary>
        Target,

        /// <summary>
        /// What the tunnel carries right now.
        /// </summary>
        Sessions,
    }

    // Resolves a failed ack to text: the agent sends localization keys, not sentences.
    private static string Describe(IpcAck ack)
    {
        return IpcMessage.TryParse(ack.Message, out var key, out var args)
            ? Loc.Instance.Get(key, args)
            : ack.Message;
    }
}
