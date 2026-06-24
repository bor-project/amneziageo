using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The per-config bypass-exclusions editor shown on a profile's Маршрутизация aspect: the manual list
/// (domains kept on the local resolver, IP/CIDR routed direct) plus the auto-exclude-LAN toggle. Saved
/// through the agent (set-config-exclusions). Moved here from the former global settings so each profile
/// carries its own. The toggle persists immediately; the list persists on «Сохранить». Both apply on the
/// next connect.
/// </summary>
internal sealed partial class ConfigExclusionsViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

    [ObservableProperty]
    private string _exclusions = string.Empty;

    [ObservableProperty]
    private bool _autoExcludeLan = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor. Seeds the backing fields directly so the auto-save in <see cref="OnAutoExcludeLanChanged"/>
    /// does not fire on construction.
    /// </summary>
    public ConfigExclusionsViewModel(AgentConnection connection, string name, string exclusions, bool autoExcludeLan)
    {
        _connection = connection;
        ConfigName = name;
        _exclusions = exclusions;
        _autoExcludeLan = autoExcludeLan;
    }

    /// <summary>The configuration name being edited.</summary>
    public string ConfigName { get; }

    // The toggle persists immediately (the textarea waits for «Сохранить»). Built once on config-open and
    // never re-seeded from snapshots, so this only fires on a real user toggle.
    partial void OnAutoExcludeLanChanged(bool value)
    {
        _ = PersistAsync();
    }

    /// <summary>
    /// Saves the exclusions list (and the current toggle) through the agent. Applies on reconnect.
    /// </summary>
    [RelayCommand]
    private Task SaveAsync()
    {
        return PersistAsync();
    }

    private async Task PersistAsync()
    {
        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConfigExclusions,
                [ConfigName, (Exclusions ?? string.Empty).Trim(), AutoExcludeLan ? "on" : "off"]));
            StatusMessage = ack.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
