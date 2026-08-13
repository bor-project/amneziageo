using Android.App;
using Android.Graphics.Drawables;
using AndroidX.Core.View;
using Avalonia.Media;
using Avalonia.Styling;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Paints the status and navigation bars in the application's own theme. Android knows nothing of the Avalonia
/// theme, so both strips keep the colour of the activity theme until they are set from here.
/// </summary>
internal static class AndroidSystemBars
{
    private static Activity? _activity;
    private static EventHandler? _themeChanged;

    /// <summary>
    /// Takes the window's bars over and follows the theme while the activity lives.
    /// </summary>
    public static void Attach(Activity activity)
    {
        Release();
        _activity = activity;
        Apply();

        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }

        _themeChanged = (_, _) => Apply();
        app.ActualThemeVariantChanged += _themeChanged;
    }

    /// <summary>
    /// Drops the activity and the theme subscription. A window replaced by the next one keeps it: the activity
    /// being torn down is destroyed after its successor is created.
    /// </summary>
    public static void Detach(Activity activity)
    {
        if (ReferenceEquals(_activity, activity))
        {
            Release();
        }
    }

    /// <summary>
    /// Re-applies the theme to the bars, for the window coming back to the foreground.
    /// </summary>
    public static void Refresh()
    {
        Apply();
    }

    private static void Release()
    {
        if (_themeChanged is not null && Avalonia.Application.Current is { } app)
        {
            app.ActualThemeVariantChanged -= _themeChanged;
        }

        _themeChanged = null;
        _activity = null;
    }

    // From target SDK 35 the bar colours are ignored and the window is laid out edge to edge, so what shows
    // behind the strips is the window's own background; older releases still take the colours.
    private static void Apply()
    {
        if (_activity?.Window is not { } window || Avalonia.Application.Current is not { } app)
        {
            return;
        }

        var page = PageColor(app);
        var color = new global::Android.Graphics.Color(page.R, page.G, page.B, page.A);
        window.SetBackgroundDrawable(new ColorDrawable(color));
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            window.SetStatusBarColor(color);
            window.SetNavigationBarColor(color);
        }

        // Drops the strip the system lays behind the navigation bar for contrast: with three-button navigation it
        // is opaque and keeps its own colour, so under a dark theme the row of buttons stays light.
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            window.NavigationBarContrastEnforced = false;
        }

        if (window.DecorView is { } decor && WindowCompat.GetInsetsController(window, decor) is { } bars)
        {
            var dark = Dark(page);
            bars.AppearanceLightStatusBars = !dark;
            bars.AppearanceLightNavigationBars = !dark;
        }
    }

    // The page colour of the current theme, falling back to the palette it is taken from.
    private static Color PageColor(Avalonia.Application app)
    {
        if (app.TryGetResource("AgBg", app.ActualThemeVariant, out var value) && value is Color color)
        {
            return color;
        }

        return app.ActualThemeVariant == ThemeVariant.Dark
            ? Color.FromRgb(0x0D, 0x10, 0x14)
            : Color.FromRgb(0xEC, 0xEE, 0xF1);
    }

    // Whether the bars stand on a dark ground and need light glyphs.
    private static bool Dark(Color color)
    {
        return ((color.R * 299) + (color.G * 587) + (color.B * 114)) / 1000 < 140;
    }
}
