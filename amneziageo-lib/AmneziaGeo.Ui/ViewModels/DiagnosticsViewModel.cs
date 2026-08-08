using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Diagnostics screen: a tab per pane — the agent log and the configuration the agent runs on. Only the pane on
/// screen reads the agent.
/// </summary>
internal sealed partial class DiagnosticsViewModel : ViewModelBase
{
    /// <summary>
    /// ctor
    /// </summary>
    public DiagnosticsViewModel(IAgentConnection connection)
    {
        Logs = new LogsViewModel(connection);
        Config = new RuntimeConfigViewModel(connection);
    }

    /// <summary>
    /// Log pane: viewer and capture settings.
    /// </summary>
    public LogsViewModel Logs { get; }

    /// <summary>
    /// Config pane: the runtime configuration and the caches behind it.
    /// </summary>
    public RuntimeConfigViewModel Config { get; }

    /// <summary>
    /// Whether the diagnostics section is the one currently shown.
    /// </summary>
    public bool IsActive { get; private set; }

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    private bool _isCompact;

    partial void OnIsCompactChanged(bool value)
    {
        Logs.IsCompact = value;
        Config.IsCompact = value;
    }

    // Which pane is showing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogTab))]
    [NotifyPropertyChangedFor(nameof(IsConfigTab))]
    private string _tab = "log";

    /// <summary>
    /// Whether the log pane is showing.
    /// </summary>
    public bool IsLogTab => Tab == "log";

    /// <summary>
    /// Whether the config pane is showing.
    /// </summary>
    public bool IsConfigTab => Tab == "config";

    partial void OnTabChanged(string value)
    {
        SyncPanes();
    }

    [RelayCommand]
    private void SelectTab(string target)
    {
        Tab = target == "config" ? "config" : "log";
    }

    /// <summary>
    /// Marks the section shown or not; the pane off screen stops reading and drops what it holds.
    /// </summary>
    public void SetActive(bool active)
    {
        IsActive = active;
        SyncPanes();
    }

    private void SyncPanes()
    {
        Logs.SetActive(IsActive && IsLogTab);
        Config.SetActive(IsActive && IsConfigTab);
    }

    /// <summary>
    /// Pushes the agent snapshot to the log pane.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        Logs.Apply(snapshot);
    }

    /// <summary>
    /// Drops what both panes hold.
    /// </summary>
    public void Reset()
    {
        IsActive = false;
        Logs.Reset();
        Config.SetActive(false);
    }
}
