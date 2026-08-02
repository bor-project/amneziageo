using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.Desktop;

/// <summary>
/// Desktop (Windows/Linux) platform services for AmneziaGeo.Ui.
/// </summary>
public static class DesktopQrScanning
{
    /// <summary>
    /// Registers the FlashCap camera scanner as the platform QR scanner.
    /// </summary>
    public static void Register()
    {
        QrCameraScannerHost.Register((preview, decoded) => new FlashCapQrScanner(preview, decoded));
    }
}
