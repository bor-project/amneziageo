using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AmneziaGeo.Localization;
using Avalonia.Media.Imaging;
using FlashCap;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Opens the default camera, shows a live preview, and decodes QR codes from frames.
/// </summary>
internal sealed class QrCameraScanner(Action<Bitmap> onPreview, Action<string> onDecoded) : IAsyncDisposable
{
    private CaptureDevice? _device;
    private volatile bool _busy;
    private volatile bool _disposed;
    private string? _last;

    /// <summary>
    /// Opens the first available camera and starts capture. Throws if no camera is present.
    /// </summary>
    public async Task StartAsync()
    {
        // Enumerate devices off the UI thread; the driver bind is slow on first call.
        var descriptor = await Task.Run(() => new CaptureDevices().EnumerateDescriptors()
            .FirstOrDefault(d => d.Characteristics.Length > 0));
        if (_disposed)
        {
            return;
        }

        if (descriptor is null)
        {
            throw new InvalidOperationException(Loc.Instance.Get("QrScanner_CameraNotFound"));
        }

        // Dispose may fire while the camera is still opening (await below); tear the device down instead of
        // leaking it. _device stays null until the opened device survives the disposed check.
        var device = await descriptor.OpenAsync(descriptor.Characteristics[0], OnFrameAsync);
        if (_disposed)
        {
            await device.DisposeAsync();
            return;
        }

        _device = device;
        await device.StartAsync();
        if (_disposed)
        {
            _device = null;
            await StopDeviceAsync(device);
        }
    }

    private async Task OnFrameAsync(PixelBufferScope scope)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        var image = scope.Buffer.CopyImage();
        try
        {
            await Task.Run(() =>
            {
                using var stream = new MemoryStream(image);
                var bitmap = new Bitmap(stream);
                onPreview(bitmap);

                var text = QrCodec.Decode(bitmap);
                if (text is not null && text != _last)
                {
                    _last = text;
                    onDecoded(text);
                }
            });
        }
        catch (Exception)
        {
            // A bad frame must not stop capture.
        }
        finally
        {
            _busy = false;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var device = _device;
        _device = null;
        if (device is not null)
        {
            await StopDeviceAsync(device);
        }
    }

    private static async Task StopDeviceAsync(CaptureDevice device)
    {
        try
        {
            await device.StopAsync();
            await device.DisposeAsync();
        }
        catch (Exception)
        {
        }
    }
}
