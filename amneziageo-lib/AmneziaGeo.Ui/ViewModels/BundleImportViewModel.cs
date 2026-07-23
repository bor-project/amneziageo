using System.Threading.Tasks;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// View model for the bundle import view.
/// </summary>
internal sealed partial class BundleImportViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;

    [ObservableProperty]
    private string _payload = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    // Name-conflict policy: 0 add-as-new (default), 1 replace, 2 skip, 3 merge.
    [ObservableProperty]
    private int _conflictPolicyIndex;

    /// <summary>
    /// ctor
    /// </summary>
    public BundleImportViewModel(IAgentConnection connection)
    {
        _connection = connection;
    }

    [RelayCommand]
    private async Task Import()
    {
        if (string.IsNullOrWhiteSpace(Payload))
        {
            StatusMessage = Loc.Instance.Get("BundleImportVm_PasteOrLoadBundle");
            return;
        }

        IsBusy = true;
        try
        {
            var policy = ConflictPolicyIndex switch { 1 => "replace", 2 => "skip", 3 => "merge", _ => "new" };
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpImportBundle, [Payload, policy]));
            StatusMessage = ack.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Загружает брошенный драгом файл бэкапа в поле импорта.
    /// </summary>
    [RelayCommand]
    private async Task LoadDroppedFile(IReadOnlyList<string>? paths)
    {
        var path = paths?.FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (path is null)
        {
            return;
        }

        try
        {
            Payload = await File.ReadAllTextAsync(path);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            Payload = string.Empty;
            StatusMessage = ex.Message;
        }
    }
}
