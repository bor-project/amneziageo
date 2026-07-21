using System;
using Avalonia.Media.Imaging;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Inline camera QR-scan view model: holds the live preview frame and status; a decoded config is
/// reported through the callback given at construction.
/// </summary>
internal sealed partial class ScanViewModel : ViewModelBase
{
    private readonly Action<VpnLinkCodec.Imported> _onResult;

    /// <summary>
    /// ctor
    /// </summary>
    public ScanViewModel(Action<VpnLinkCodec.Imported> onResult)
    {
        _onResult = onResult;
    }

    [ObservableProperty]
    private Bitmap? _preview;

    [ObservableProperty]
    private string _statusMessage = Loc.Instance.Get("ScanVm_AimCameraAtQr");

    /// <summary>
    /// The decoded config once a valid QR is read.
    /// </summary>
    public VpnLinkCodec.Imported? Result { get; private set; }

    /// <summary>
    /// Reports a decoded config to the create form.
    /// </summary>
    public void ReportResult(VpnLinkCodec.Imported imported)
    {
        Result = imported;
        _onResult(imported);
    }
}
