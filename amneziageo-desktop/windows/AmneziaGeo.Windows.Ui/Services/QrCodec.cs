using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using QRCoder;
using ZXing;
using ZXing.Common;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// QR generation and decoding without a System.Drawing dependency.
/// </summary>
internal static class QrCodec
{
    /// <summary>
    /// Renders text as a QR-code bitmap.
    /// </summary>
    public static Bitmap Generate(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
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

        var stride = width * 4;
        var pixels = new byte[stride * height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
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
}
