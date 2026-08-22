using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Diagnostics screen: one viewer over every source the agent answers from - the stored tables, what the tunnel
/// carries right now, the configuration it runs on and the caches behind it.
/// </summary>
internal sealed class DiagnosticsViewModel : ViewModelBase
{
    /// <summary>
    /// ctor
    /// </summary>
    public DiagnosticsViewModel(MainWindowViewModel host, IAgentConnection connection)
    {
        Logs = new LogsViewModel(host, connection);
    }

    /// <summary>
    /// The viewer: the source, what it records and the body it fills.
    /// </summary>
    public LogsViewModel Logs { get; }

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
        Logs.Reset();
    }
}
