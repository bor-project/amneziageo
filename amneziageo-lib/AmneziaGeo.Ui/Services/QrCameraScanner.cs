using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Live camera QR scanner: previews frames and reports decoded QR payloads until disposed.
/// </summary>
internal interface IQrCameraScanner : IAsyncDisposable
{
    /// <summary>
    /// Opens the camera and starts capture. Throws when no usable camera is present.
    /// </summary>
    Task StartAsync();
}

/// <summary>
/// Builds the platform camera scanner wired to a preview sink and a decoded-text sink.
/// </summary>
internal delegate IQrCameraScanner QrCameraScannerFactory(Action<Bitmap> onPreview, Action<string> onDecoded);

/// <summary>
/// Runtime registry of the platform camera scanner. The host registers its implementation at startup; screens
/// build scanners through it. No registration means the platform has no camera scanner.
/// </summary>
internal static class QrCameraScannerHost
{
    private static QrCameraScannerFactory? _factory;

    /// <summary>
    /// Whether a camera scanner is available on this platform.
    /// </summary>
    public static bool IsAvailable => _factory is not null;

    /// <summary>
    /// Registers the platform scanner factory.
    /// </summary>
    public static void Register(QrCameraScannerFactory factory) => _factory = factory;

    /// <summary>
    /// Builds a scanner wired to the sinks, or null when the platform has none.
    /// </summary>
    public static IQrCameraScanner? Create(Action<Bitmap> onPreview, Action<string> onDecoded) => _factory?.Invoke(onPreview, onDecoded);
}
