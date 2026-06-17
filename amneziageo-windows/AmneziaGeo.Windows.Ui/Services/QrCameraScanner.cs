using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using FlashCap;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Opens the default camera with FlashCap, raises each frame as an Avalonia bitmap for a live preview,
/// and decodes QR codes from the frames (ZXing). One frame is decoded at a time; a newly-seen QR text is
/// reported via the callback. Pure-managed — no native dependencies beyond the OS camera APIs.
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
            throw new InvalidOperationException("Камера не найдена");
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
            // A mid-stream / undecodable frame must not stop capture.
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
