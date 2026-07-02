using System.Threading.Tasks;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// View model for the selective bundle import dialog (#91): the user pastes or loads a bundle JSON (as
/// produced by <see cref="BundleExportDialogViewModel"/>) and imports it via
/// <see cref="IpcContract.OpImportBundle"/>. No tree preview - the agent's name-conflict policy (automatic
/// dedup-by-rename, same as the existing single-profile import) makes one low-value.
/// </summary>
internal sealed partial class BundleImportDialogViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

    [ObservableProperty]
    private string _payload = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public BundleImportDialogViewModel(AgentConnection connection)
    {
        _connection = connection;
    }

    [RelayCommand]
    private async Task Import()
    {
        if (string.IsNullOrWhiteSpace(Payload))
        {
            StatusMessage = "Вставьте или загрузите содержимое файла бандла.";
            return;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpImportBundle, [Payload]));
            StatusMessage = ack.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
