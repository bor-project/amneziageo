using Avalonia.Media.Imaging;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// View model for the export dialog: fetches a config's wg-quick text from the agent and renders it as
/// raw <c>.conf</c> or an Amnezia <c>vpn://</c> link, with a matching QR code, for copy / save.
/// </summary>
internal sealed partial class ExportDialogViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private string _confText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConf))]
    [NotifyPropertyChangedFor(nameof(IsLink))]
    private bool _asLink;

    [ObservableProperty]
    private string _payload = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQr))]
    private Bitmap? _qrImage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isReady;

    /// <summary>
    /// ctor
    /// </summary>
    public ExportDialogViewModel(AgentConnection connection, string name)
    {
        _connection = connection;
        ConfigName = name;
    }

    /// <summary>The config being exported.</summary>
    public string ConfigName { get; }

    /// <summary>Whether the raw .conf form is selected.</summary>
    public bool IsConf => !AsLink;

    /// <summary>Whether the vpn:// link form is selected.</summary>
    public bool IsLink => AsLink;

    /// <summary>Whether a QR code was rendered for the current payload.</summary>
    public bool HasQr => QrImage is not null;

    /// <summary>A suggested file name for the current form.</summary>
    public string SuggestedFileName => AsLink ? $"{ConfigName}.vpn.txt" : $"{ConfigName}.conf";

    /// <summary>
    /// Loads the config text from the agent and renders the initial form.
    /// </summary>
    public async Task LoadAsync()
    {
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetConfig, [ConfigName]));
        if (!ack.Ok)
        {
            StatusMessage = ack.Message;
            return;
        }

        _confText = ack.Message;
        IsReady = true;
        Refresh();
    }

    partial void OnAsLinkChanged(bool value)
    {
        Refresh();
    }

    private void Refresh()
    {
        if (!IsReady)
        {
            return;
        }

        Payload = AsLink ? VpnLinkCodec.Encode(_confText, ConfigName) : _confText;
        try
        {
            QrImage = QrCodec.Generate(Payload);
            StatusMessage = string.Empty;
        }
        catch (Exception)
        {
            QrImage = null;
            StatusMessage = "Слишком длинно для QR — используйте файл или ссылку.";
        }
    }

    [RelayCommand]
    private void ShowConf()
    {
        AsLink = false;
    }

    [RelayCommand]
    private void ShowLink()
    {
        AsLink = true;
    }
}
