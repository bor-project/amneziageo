using Avalonia.Media.Imaging;
using AmneziaGeo.Windows.Ui.Services;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// View model for the camera QR-scan dialog: holds the live preview frame and status; the decoded config
/// is stored in Result when a valid QR is recognised.
/// </summary>
internal sealed partial class ScanDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private Bitmap? _preview;

    [ObservableProperty]
    private string _statusMessage = Loc.Instance.Get("ScanVm_AimCameraAtQr");

    /// <summary>
    /// The decoded config once a valid QR is read.
    /// </summary>
    public VpnLinkCodec.Imported? Result { get; set; }
}
