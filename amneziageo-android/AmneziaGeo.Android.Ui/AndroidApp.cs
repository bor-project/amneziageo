using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Приложение Android: поднимает Avalonia в процессе окна, окно строит первая активность.
/// </summary>
[Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<App>
{
    /// <summary>
    /// ctor
    /// </summary>
    public AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    /// <summary>
    /// Идёт ли код в процессе окна, а не в процессе туннеля.
    /// </summary>
    internal static bool IsWindowProcess { get; } = !CurrentProcess().EndsWith(":vpn", StringComparison.Ordinal);

    /// <inheritdoc/>
    public override void OnCreate()
    {
        // Процесс туннеля ничего не рисует.
        if (!IsWindowProcess)
        {
            return;
        }

        base.OnCreate();
    }

    /// <summary>
    /// Picks the drawing path, overridable by a `render` file holding software or vulkan next to the app data:
    /// the Mali blobs in TV boxes leave stale pixels on screen, and only the device shows which path draws right.
    /// </summary>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Текст, и моноширинный тоже, берёт у системных шрифтов начертание запрошенного веса.
        return base.CustomizeAppBuilder(builder)
            .With(new AndroidPlatformOptions { RenderingMode = RenderingModes() })
            .With(WeightedSystemFonts.Options("sans-serif", "monospace"));
    }

    private static IReadOnlyList<AndroidRenderingMode> RenderingModes()
    {
        var requested = ReadRenderOverride();
        if (string.Equals(requested, "software", StringComparison.OrdinalIgnoreCase))
        {
            return [AndroidRenderingMode.Software];
        }

        if (string.Equals(requested, "vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return [AndroidRenderingMode.Vulkan, AndroidRenderingMode.Egl, AndroidRenderingMode.Software];
        }

        return [AndroidRenderingMode.Egl, AndroidRenderingMode.Software];
    }

    private static string ReadRenderOverride()
    {
        try
        {
            var folder = global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
            if (folder is null)
            {
                return string.Empty;
            }

            var path = Path.Combine(folder, "render");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    // Имя процесса, в котором поднято приложение.
    private static string CurrentProcess()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            return global::Android.App.Application.ProcessName ?? string.Empty;
        }

        try
        {
            return File.ReadAllText("/proc/self/cmdline").TrimEnd('\0');
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
