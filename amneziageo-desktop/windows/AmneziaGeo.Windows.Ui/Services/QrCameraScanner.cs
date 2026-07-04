using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AmneziaGeo.Localization;
using Avalonia.Media.Imaging;
using FlashCap;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Opens the default camera, shows a live preview, and decodes QR codes from frames.
/// </summary>
internal sealed class QrCameraScanner(Action<Bitmap> onPreview, Action<string> onDecoded) : IAsyncDisposable
{
    private CaptureDevice? _device;
    private volatile bool _busy;
    private string? _last;

    /// <summary>
    /// Opens the first available camera and starts capture. Throws if no camera is present.
    /// </summary>
    public async Task StartAsync()
    {
        var descriptor = new CaptureDevices().EnumerateDescriptors()
            .FirstOrDefault(d => d.Characteristics.Length > 0);
        if (descriptor is null)
        {
            throw new InvalidOperationException(Loc.Instance.Get("QrScanner_CameraNotFound"));
        }

        _device = await descriptor.OpenAsync(descriptor.Characteristics[0], OnFrameAsync);
        await _device.StartAsync();
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
        if (_device is not null)
        {
            try
            {
                await _device.StopAsync();
            }
            catch (Exception)
            {
            }

            await _device.DisposeAsync();
            _device = null;
        }
    }
}
