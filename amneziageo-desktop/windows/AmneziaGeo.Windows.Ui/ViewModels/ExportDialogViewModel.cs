using Avalonia.Media.Imaging;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// View model for the export dialog: fetches a config's wg-quick text from the agent and renders it as raw .conf or an Amnezia vpn:// link, with a matching QR code, for copy / save.
/// </summary>
internal sealed partial class ExportDialogViewModel : ViewModelBase, IEditScope
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
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    private Bitmap? _qrImage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    private bool _isReady;

    // Inline editing of the .conf text; "Сохранить" persists in place.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQr))]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    private bool _isEditing;

    /// <summary>
    /// ctor
    /// </summary>
    public ExportDialogViewModel(AgentConnection connection, string name)
    {
        _connection = connection;
        ConfigName = name;
    }

    /// <summary>
    /// The config being exported.
    /// </summary>
    public string ConfigName { get; }

    /// <summary>
    /// Whether the raw .conf form is selected.
    /// </summary>
    public bool IsConf => !AsLink;

    /// <summary>
    /// Whether the vpn:// link form is selected.
    /// </summary>
    public bool IsLink => AsLink;

    /// <summary>
    /// Whether a QR code was rendered for the current payload.
    /// </summary>
    public bool HasQr => QrImage is not null;

    /// <summary>
    /// Whether the QR is shown: a QR exists and the text is not being edited.
    /// </summary>
    public bool ShowQr => QrImage is not null && !IsEditing;

    /// <summary>
    /// Whether the payload is loaded but no QR could be produced (the config is too large to encode) and we are not editing.
    /// </summary>
    public bool QrUnavailable => IsReady && !IsEditing && QrImage is null;

    /// <summary>
    /// A suggested file name for the current form.
    /// </summary>
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
            // Too large to encode as a QR.
            QrImage = null;
            StatusMessage = string.Empty;
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

    // "Изменить": unlock the .conf text for editing.
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
    private async Task SaveEdit() => await SaveEditInternalAsync();

    private async Task<bool> SaveEditInternalAsync()
    {
        if (!CanCommit())
        {
            return false;
        }

        var text = Payload.Trim();
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpEditConfig, [ConfigName, text]));
        if (!ack.Ok)
        {
            StatusMessage = ack.Message;
            return false;
        }

        _confText = text;
        IsEditing = false;
        Refresh();
        StatusMessage = Loc.Instance.Get("ExportVm_Saved");
        return true;
    }

    // ---- IEditScope (#143): the .conf inline editor already commits explicitly; joining the edit model blocks
    // navigation while a .conf edit is open (previously switching config mid-edit silently discarded it) and lets
    // the header Save/Cancel drive it too. "Dirty" == in edit mode; Begin/Save/Cancel keep their local buttons. ----

    /// <inheritdoc />
    public bool IsDirty => IsEditing;

    /// <inheritdoc />
    public event EventHandler? DirtyChanged;

    partial void OnIsEditingChanged(bool value) => DirtyChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public bool CanCommit()
    {
        if (!IsEditing)
        {
            return true;
        }

        var text = Payload.Trim();
        if (text.Length == 0
            || !text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = Loc.Instance.Get("ExportVm_NotWireGuardConfig");
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void CaptureBaseline()
    {
        // Nothing to capture: IsEditing is itself the dirty flag, cleared by Save (commit) or Cancel (revert).
    }

    /// <inheritdoc />
    public void Revert()
    {
        if (IsEditing)
        {
            IsEditing = false;
            Refresh();
        }
    }

    /// <inheritdoc />
    public Task<bool> CommitAsync() => IsEditing ? SaveEditInternalAsync() : Task.FromResult(true);
}
