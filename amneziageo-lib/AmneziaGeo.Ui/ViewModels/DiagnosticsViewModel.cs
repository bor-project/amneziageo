using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Diagnostics screen: a tab per pane — the agent log, the configuration the agent runs on, and the checks it
/// can run. Only the pane on screen reads the agent.
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
        Check = new CheckViewModel(connection);
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
    /// Check pane: the channel ladder and the targeted check, with the verdict they produced.
    /// </summary>
    public CheckViewModel Check { get; }

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
        Check.IsCompact = value;
    }

    // Which pane is showing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogTab))]
    [NotifyPropertyChangedFor(nameof(IsConfigTab))]
    [NotifyPropertyChangedFor(nameof(IsCheckTab))]
    private string _tab = "log";

    /// <summary>
    /// Whether the log pane is showing.
    /// </summary>
    public bool IsLogTab => Tab == "log";

    /// <summary>
    /// Whether the config pane is showing.
    /// </summary>
    public bool IsConfigTab => Tab == "config";

    /// <summary>
    /// Whether the check pane is showing.
    /// </summary>
    public bool IsCheckTab => Tab == "check";

    partial void OnTabChanged(string value)
    {
        SyncPanes();
    }

    [RelayCommand]
    private void SelectTab(string target)
    {
        Tab = target is "config" or "check" ? target : "log";
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
        Check.SetActive(IsActive && IsCheckTab);
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
        Check.SetActive(false);
    }
}
