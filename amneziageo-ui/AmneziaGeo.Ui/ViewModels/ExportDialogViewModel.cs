using Avalonia.Media.Imaging;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// What the config area shows.
/// </summary>
internal enum ConfigViewMode
{
    /// <summary>QR of the raw .conf text.</summary>
    QrConf,

    /// <summary>QR of the Amnezia vpn:// link.</summary>
    QrLink,

    /// <summary>The .conf text, editable in place.</summary>
    Text,
}

/// <summary>
/// View model for the config area: fetches a config's wg-quick text from the agent and shows it either as a QR (of the raw .conf or of an Amnezia vpn:// link) or as the text itself, edited in place under the atomic edit model (#143).
/// </summary>
internal sealed partial class ExportDialogViewModel : ViewModelBase, IEditScope
{
    private readonly IAgentConnection _connection;
    private string _baseConfText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQrConf))]
    [NotifyPropertyChangedFor(nameof(IsQrLink))]
    [NotifyPropertyChangedFor(nameof(IsText))]
    [NotifyPropertyChangedFor(nameof(ShowQr))]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    private ConfigViewMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _confText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQr))]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    private Bitmap? _qrImage;

    [ObservableProperty]
    private string _linkText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    private bool _isReady;

    /// <summary>
    /// ctor
    /// </summary>
    public ExportDialogViewModel(IAgentConnection connection, string name)
    {
        _connection = connection;
        ConfigName = name;
    }

    /// <summary>
    /// The config being shown.
    /// </summary>
    public string ConfigName { get; }

    /// <summary>
    /// Whether the .conf QR is selected.
    /// </summary>
    public bool IsQrConf => Mode == ConfigViewMode.QrConf;

    /// <summary>
    /// Whether the vpn:// link QR is selected.
    /// </summary>
    public bool IsQrLink => Mode == ConfigViewMode.QrLink;

    /// <summary>
    /// Whether the config text is selected.
    /// </summary>
    public bool IsText => Mode == ConfigViewMode.Text;

    /// <summary>
    /// Whether a QR is rendered for the selected mode.
    /// </summary>
    public bool ShowQr => !IsText && QrImage is not null;

    /// <summary>
    /// Whether the config is loaded but no QR could be produced (it is too large to encode).
    /// </summary>
    public bool QrUnavailable => IsReady && !IsText && QrImage is null;

    /// <summary>
    /// Content copied by the export card: the vpn link in link mode, otherwise the config text.
    /// </summary>
    public string Payload => IsQrLink ? LinkText : ConfText;

    /// <summary>
    /// Loads the config text from the agent and renders its QR.
    /// </summary>
    public async Task LoadAsync()
    {
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetConfig, [ConfigName]));
        if (!ack.Ok)
        {
            StatusMessage = ack.Message;
            return;
        }

        Seed(ack.Message);
    }

    /// <summary>
    /// Adopts the config text as the clean baseline and renders its QR.
    /// </summary>
    public void Seed(string confText)
    {
        // Baseline before the field, so seeding it does not read as a dirty edit (#143).
        _baseConfText = confText;
        ConfText = confText;
        IsReady = true;
        Refresh();
    }

    partial void OnModeChanged(ConfigViewMode value) => Refresh();

    partial void OnConfTextChanged(string value)
    {
        // Any edit clears a stale validation / status line (#3).
        StatusMessage = string.Empty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    // Re-encodes the baseline for the selected QR mode. The text mode carries no QR, so it skips the work.
    private void Refresh()
    {
        if (!IsReady || IsText)
        {
            LinkText = string.Empty;
            return;
        }

        var payload = IsQrLink ? VpnLinkCodec.Encode(_baseConfText, ConfigName) : _baseConfText;
        LinkText = IsQrLink ? payload : string.Empty;
        _ = RefreshQrAsync(payload);
    }

    // Discards a stale QR build.
    private int _qrRefreshToken;

    private async Task RefreshQrAsync(string payload)
    {
        var token = ++_qrRefreshToken;
        var image = await Task.Run(() => TryEncodeQr(payload));
        if (token != _qrRefreshToken)
        {
            return;
        }

        QrImage = image;
    }

    private static Bitmap? TryEncodeQr(string payload)
    {
        try
        {
            return QrCodec.Generate(payload);
        }
        catch (Exception)
        {
            // Too large to encode as a QR.
            return null;
        }
    }

    [RelayCommand]
    private void ShowQrConf() => Mode = ConfigViewMode.QrConf;

    [RelayCommand]
    private void ShowQrLink() => Mode = ConfigViewMode.QrLink;

    [RelayCommand]
    private void ShowText() => Mode = ConfigViewMode.Text;

    // ---- IEditScope (#143): the .conf text is edited in place, with no local Save. Typing marks the config
    // item dirty, which blocks navigation and hands the commit to the header Save / Cancel. ----

    /// <inheritdoc />
    public bool IsDirty => !string.Equals(ConfText, _baseConfText, StringComparison.Ordinal);

    /// <inheritdoc />
    public event EventHandler? DirtyChanged;

    /// <inheritdoc />
    public bool CanCommit()
    {
        var text = ConfText.Trim();
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
        _baseConfText = ConfText;
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <inheritdoc />
    public void Revert()
    {
        ConfText = _baseConfText;
    }

    /// <inheritdoc />
    public async Task<bool> CommitAsync()
    {
        if (!CanCommit())
        {
            return false;
        }

        var text = ConfText.Trim();
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpEditConfig, [ConfigName, text]));
        if (!ack.Ok)
        {
            StatusMessage = ack.Message;
            return false;
        }

        _baseConfText = text;
        ConfText = text;
        StatusMessage = Loc.Instance.Get("ExportVm_Saved");
        return true;
    }
}
