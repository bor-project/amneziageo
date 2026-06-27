using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The per-config preferred-DNS editor shown on a profile's DNS aspect: a single comma/space-separated
/// servers field saved through the agent (set-config-dns). Empty clears the override → auto-detect the
/// system resolvers. Moved here from the former global settings so each profile carries its own DNS for
/// NON-tunneled names. Applies on the next connect.
/// </summary>
internal sealed partial class ConfigDnsViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

    [ObservableProperty]
    private string _dns = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigDnsViewModel(AgentConnection connection, string name, string dns)
    {
        _connection = connection;
        ConfigName = name;
        _dns = dns;
    }

    /// <summary>The configuration name being edited.</summary>
    public string ConfigName { get; }

    /// <summary>
    /// Saves the preferred DNS through the agent (empty clears it → auto-detect). Applies on reconnect.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConfigDns,
                [ConfigName, (Dns ?? string.Empty).Trim()]));
            StatusMessage = ack.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
