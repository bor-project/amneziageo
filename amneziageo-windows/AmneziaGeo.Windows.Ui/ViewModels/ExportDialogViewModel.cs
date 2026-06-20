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
    [NotifyPropertyChangedFor(nameof(ShowQr))]
    private Bitmap? _qrImage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isReady;

    // Inline editing of the .conf text: the payload box is read-only until "Изменить" unlocks it. Editing
    // applies to the raw config form (not the vpn:// link), and "Сохранить" persists it in place.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQr))]
    private bool _isEditing;

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

    /// <summary>Whether the QR is shown: a QR exists and the text is not being edited.</summary>
    public bool ShowQr => QrImage is not null && !IsEditing;

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

    // "Изменить": unlock the text for editing. Force the raw .conf form first — editing applies to the
    // config text, not the vpn:// link.
    [RelayCommand]
    private void BeginEdit()
    {
        if (!IsReady)
        {
            return;
        }

        AsLink = false;
        StatusMessage = string.Empty;
        IsEditing = true;
    }

    // "Отмена": discard edits, revert the text, lock it again.
    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Refresh();
    }

    // "Сохранить": persist the edited .conf in place (edit-config), then lock + re-render.
    [RelayCommand]
    private async Task SaveEdit()
    {
        var text = Payload.Trim();
        if (text.Length == 0
            || !text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Не похоже на конфигурацию WireGuard/AmneziaWG (нужны [Interface] и [Peer]).";
            return;
        }

        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpEditConfig, [ConfigName, text]));
        if (!ack.Ok)
        {
            StatusMessage = ack.Message;
            return;
        }

        _confText = text;
        IsEditing = false;
        Refresh();
        StatusMessage = "Сохранено.";
    }
}
