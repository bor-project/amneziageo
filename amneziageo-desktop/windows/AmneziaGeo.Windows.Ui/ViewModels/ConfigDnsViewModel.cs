using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The per-config preferred-DNS editor: a comma/space-separated servers field saved through the agent. Empty clears the override to auto-detect the system resolvers. Applies on the next connect.
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

    /// <summary>
    /// The configuration name being edited.
    /// </summary>
    public string ConfigName { get; }

    /// <summary>
    /// Saves the preferred DNS through the agent. Empty clears the override. Applies on reconnect.
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
