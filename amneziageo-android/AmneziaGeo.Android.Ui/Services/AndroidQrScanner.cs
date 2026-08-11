using System;
using System.Threading.Tasks;
using Android.Content;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Java.Util.Concurrent;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;
using JavaObject = Java.Lang.Object;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// CameraX QR scanner for Android: binds an RGBA image-analysis use case, previews frames, and decodes QR codes
/// from them with the shared ZXing decoder (no Google Play Services, so it works on Huawei/EMUI too).
/// </summary>
internal sealed class AndroidQrScanner(Action<AvBitmap> onPreview, Action<string> onDecoded) : IQrCameraScanner
{
    private ProcessCameraProvider? _provider;
    private IExecutorService? _executor;
    private ScannerLifecycleOwner? _lifecycle;
    private volatile bool _disposed;

    /// <inheritdoc/>
    public async Task StartAsync()
    {
        var activity = MainActivity.Current
            ?? throw new InvalidOperationException(Loc.Instance.Get("QrScanner_CameraNotFound"));

        if (!await activity.RequestCameraPermissionAsync())
        {
            throw new InvalidOperationException(Loc.Instance.Get("QrScanner_CameraPermissionDenied"));
        }

        if (_disposed)
        {
            return;
        }

        var provider = await GetProviderAsync(activity);
        if (_disposed)
        {
            provider.UnbindAll();
            return;
        }

        _provider = provider;
        _executor = Executors.NewSingleThreadExecutor();

        var builder = new ImageAnalysis.Builder();
        builder.SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest);
        builder.SetOutputImageFormat(ImageAnalysis.OutputImageFormatRgba8888);
        var analysis = builder.Build()!;
        analysis.SetAnalyzer(_executor, new FrameAnalyzer(onPreview, onDecoded));

        var lifecycle = new ScannerLifecycleOwner();
        _lifecycle = lifecycle;

        // BindToLifecycle and the lifecycle registry must be driven on the main thread.
        var tcs = new TaskCompletionSource<bool>();
        activity.RunOnUiThread(() =>
        {
            try
            {
                lifecycle.Start();
                provider.UnbindAll();
                provider.BindToLifecycle(lifecycle, CameraSelector.DefaultBackCamera!, analysis);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        await tcs.Task;
    }

    private static Task<ProcessCameraProvider> GetProviderAsync(Context context)
    {
        var tcs = new TaskCompletionSource<ProcessCameraProvider>();
        var future = ProcessCameraProvider.GetInstance(context);
        future.AddListener(new ActionRunnable(() =>
        {
            try
            {
                tcs.TrySetResult((ProcessCameraProvider)future.Get()!);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }), ContextCompat.GetMainExecutor(context));
        return tcs.Task;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var provider = _provider;
        var lifecycle = _lifecycle;
        var executor = _executor;
        _provider = null;
        _lifecycle = null;
        _executor = null;

        var activity = MainActivity.Current;
        if (provider is not null && activity is not null)
        {
            var tcs = new TaskCompletionSource<bool>();
            activity.RunOnUiThread(() =>
            {
                try
                {
                    provider.UnbindAll();
                    lifecycle?.Destroy();
                }
                catch (Exception)
                {
                }

                tcs.TrySetResult(true);
            });
            await tcs.Task;
        }

        executor?.Shutdown();
    }

    // Converts each RGBA analysis frame to an Avalonia bitmap for preview and decodes a QR from it.
    private sealed class FrameAnalyzer(Action<AvBitmap> onPreview, Action<string> onDecoded) : JavaObject, ImageAnalysis.IAnalyzer
    {
        private string? _last;

        public void Analyze(IImageProxy? image)
        {
            if (image is null)
            {
                return;
            }

            try
            {
                var bitmap = ToBitmap(image);
                if (bitmap is null)
                {
                    return;
                }

                onPreview(bitmap);
                var text = QrCodec.Decode(bitmap);
                if (text is not null && text != _last)
                {
                    _last = text;
                    onDecoded(text);
                }
            }
            catch (Exception)
            {
                // A bad frame must not stop capture.
            }
            finally
            {
                image.Close();
            }
        }

        private static unsafe WriteableBitmap? ToBitmap(IImageProxy image)
        {
            var planes = image.GetPlanes();
            if (planes is null || planes.Length == 0)
            {
                return null;
            }

            var plane = planes[0];
            var buffer = plane.Buffer;
            if (buffer is null)
            {
                return null;
            }

            var width = image.Width;
            var height = image.Height;
            var rowStride = plane.RowStride;
            var pixelStride = plane.PixelStride;

            // Analysis frames come in sensor orientation; this turns them upright for the preview.
            var turn = Turn(image.ImageInfo?.RotationDegrees ?? 0);
            var sideways = turn is 90 or 270;

            var src = new byte[buffer.Remaining()];
            buffer.Get(src);

            var size = new PixelSize(sideways ? height : width, sideways ? width : height);
            var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var frame = bitmap.Lock())
            {
                var dst = (byte*)frame.Address;
                var dstStride = frame.RowBytes;
                for (var y = 0; y < height; y++)
                {
                    var srcRow = y * rowStride;
                    for (var x = 0; x < width; x++)
                    {
                        var s = srcRow + (x * pixelStride);
                        var d = Target(turn, x, y, width, height, dstStride);
                        // Source is RGBA; the Avalonia bitmap is BGRA.
                        dst[d + 0] = src[s + 2];
                        dst[d + 1] = src[s + 1];
                        dst[d + 2] = src[s + 0];
                        dst[d + 3] = src[s + 3];
                    }
                }
            }

            return bitmap;
        }

        // Rounds the frame rotation to a right angle.
        private static int Turn(int degrees) => (((degrees % 360) + 360) % 360) switch
        {
            >= 45 and < 135 => 90,
            >= 135 and < 225 => 180,
            >= 225 and < 315 => 270,
            _ => 0,
        };

        // Places a source pixel in the turned bitmap.
        private static int Target(int turn, int x, int y, int width, int height, int stride) => turn switch
        {
            90 => (x * stride) + ((height - 1 - y) * 4),
            180 => ((height - 1 - y) * stride) + ((width - 1 - x) * 4),
            270 => ((width - 1 - x) * stride) + (y * 4),
            _ => (y * stride) + (x * 4),
        };
    }

    // Self-driven lifecycle owner: CameraX binds a use case to it; the scanner moves it to Resumed on start and
    // Destroyed on dispose so capture stops when the scan view goes away.
    private sealed class ScannerLifecycleOwner : JavaObject, ILifecycleOwner
    {
        private readonly LifecycleRegistry _registry;

        /// <summary>
        /// ctor
        /// </summary>
        public ScannerLifecycleOwner()
        {
            _registry = new LifecycleRegistry(this);
        }

        public Lifecycle Lifecycle => _registry;

        public void Start() => _registry.SetCurrentState(Lifecycle.State.Resumed!);

        public void Destroy() => _registry.SetCurrentState(Lifecycle.State.Destroyed!);
    }

    // Adapts an Action to a Java Runnable for the camera-provider future listener.
    private sealed class ActionRunnable(Action action) : JavaObject, Java.Lang.IRunnable
    {
        public void Run() => action();
    }
}
