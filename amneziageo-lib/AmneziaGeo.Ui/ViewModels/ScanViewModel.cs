using System;
using Avalonia.Media.Imaging;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Inline camera QR-scan view model: holds the live preview frame and status. A decoded QR's raw text is
/// handed to the accept callback given at construction, which decodes it for its own screen and returns whether
/// it was recognised; an unrecognised payload keeps the scanner running with a hint.
/// </summary>
internal sealed partial class ScanViewModel : ViewModelBase
{
    private readonly Func<string, bool> _tryAccept;

    /// <summary>
    /// ctor
    /// </summary>
    public ScanViewModel(Func<string, bool> tryAccept)
    {
        _tryAccept = tryAccept;
    }

    [ObservableProperty]
    private Bitmap? _preview;

    [ObservableProperty]
    private string _statusMessage = Loc.Instance.Get("ScanVm_AimCameraAtQr");

    /// <summary>
    /// Hands a decoded QR's raw text to the owning screen; on an unrecognised payload keeps scanning with a hint.
    /// </summary>
    public void ReportRaw(string text)
    {
        if (!_tryAccept(text))
        {
            StatusMessage = Loc.Instance.Get("ScanCode_QrNotConfig");
        }
    }
}
