using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Android platform QR scanner registration.
/// </summary>
internal static class AndroidQrScanning
{
    /// <summary>
    /// Registers the CameraX camera scanner as the platform QR scanner.
    /// </summary>
    public static void Register()
    {
        QrCameraScannerHost.Register((preview, decoded) => new AndroidQrScanner(preview, decoded));
    }
}
