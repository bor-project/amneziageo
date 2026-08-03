using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using QRCoder;
using ZXing;
using ZXing.Common;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// QR generation and decoding without a System.Drawing dependency.
/// </summary>
internal static class QrCodec
{
    // Colours forced to RGBA: the colourless overload writes a greyscale PNG, which Avalonia keeps as Gray8 and
    // then refuses to hand back through CopyPixels - a saved QR could not be read back in.
    private static readonly byte[] _dark = [0, 0, 0, 255];
    private static readonly byte[] _light = [255, 255, 255, 255];

    /// <summary>
    /// Renders text as a QR-code bitmap.
    /// </summary>
    public static Bitmap Generate(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule, _dark, _light);
        return new Bitmap(new MemoryStream(png));
    }

    /// <summary>
    /// Decodes the first QR code found in an image bitmap, or null if none is readable.
    /// </summary>
    public static string? Decode(Bitmap bitmap)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var pixels = ReadBgra(bitmap, width, height);
        if (pixels is null)
        {
            return null;
        }

        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = [BarcodeFormat.QR_CODE],
            },
        };

        return reader.Decode(source)?.Text;
    }

    // Reads the bitmap as BGRA32. Avalonia exposes the pixels only for the formats it knows, so a greyscale or
    // paletted image (a scanned or third-party QR) goes through a render target that normalizes it.
    private static byte[]? ReadBgra(Bitmap bitmap, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var rect = new PixelRect(0, 0, width, height);
        try
        {
            CopyInto(bitmap, rect, pixels, stride);
            return pixels;
        }
        catch (NotSupportedException)
        {
            return Redraw(bitmap, rect, pixels, stride) ? pixels : null;
        }
    }

    private static bool Redraw(Bitmap bitmap, PixelRect rect, byte[] pixels, int stride)
    {
        try
        {
            using var target = new RenderTargetBitmap(rect.Size);
            using (var context = target.CreateDrawingContext())
            {
                context.DrawImage(bitmap, new Rect(0, 0, rect.Width, rect.Height));
            }

            CopyInto(target, rect, pixels, stride);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CopyInto(Bitmap bitmap, PixelRect rect, byte[] pixels, int stride)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(rect, handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }
    }
}
