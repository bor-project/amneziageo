using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Diagnostics screen: one viewer over every source the agent answers from - the stored tables, what the tunnel
/// carries right now, the configuration it runs on and the caches behind it, plus the archive support asks for.
/// </summary>
internal sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;

    /// <summary>
    /// ctor
    /// </summary>
    public DiagnosticsViewModel(IAgentConnection connection, LogsViewModel logs)
    {
        _connection = connection;
        Logs = logs;
    }

    /// <summary>
    /// The viewer: the source, what it records and the body it fills.
    /// </summary>
    public LogsViewModel Logs { get; }

    /// <summary>
    /// Result line for the diagnostics archive: where it was written, or why it was not.
    /// </summary>
    [ObservableProperty]
    private string _archiveStatus = string.Empty;

    /// <summary>
    /// Whether the archive is being built right now.
    /// </summary>
    [ObservableProperty]
    private bool _archiveRunning;

    /// <summary>
    /// Narrow-window layout flag, pushed by the shell.
    /// </summary>
    public bool IsCompact
    {
        get => Logs.IsCompact;
        set => Logs.IsCompact = value;
    }

    /// <summary>
    /// Marks the section shown or not; off screen the viewer stops reading and drops what it holds.
    /// </summary>
    public void SetActive(bool active)
    {
        Logs.SetActive(active);
    }

    /// <summary>
    /// Pushes the agent snapshot to the viewer.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        Logs.Apply(snapshot);
    }

    /// <summary>
    /// Drops what the viewer holds.
    /// </summary>
    public void Reset()
    {
        ArchiveStatus = string.Empty;
        Logs.Reset();
    }

    /// <summary>
    /// Asks the agent for a redacted diagnostics archive and returns the path it wrote, null when it wrote none.
    /// The agent builds it under its own account, so the window offers it for saving instead of moving it.
    /// </summary>
    public async Task<string?> CollectArchiveAsync()
    {
        ArchiveRunning = true;
        ArchiveStatus = Loc.Instance.Get("Main_ArchiveRunning");
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpCollectDiagnostics, []));
            ArchiveStatus = ack.Ok ? Loc.Instance.Get("Main_ArchiveAt", ack.Message) : ack.Message;
            return ack.Ok ? ack.Message : null;
        }
        catch (Exception ex)
        {
            _ = LogToAgentAsync($"diagnostics collect failed: {ex}");
            ArchiveStatus = Loc.Instance.Get("Main_ArchiveFailed");
            return null;
        }
        finally
        {
            ArchiveRunning = false;
        }
    }

    // Forwards a diagnostic line to the agent log; the UI process keeps no log of its own.
    private async Task LogToAgentAsync(string message)
    {
        try
        {
            await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpLogClient, [message]));
        }
        catch
        {
            // Best-effort: the failure is already surfaced to the user.
        }
    }
}
