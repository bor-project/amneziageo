using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// The Avalonia application.
/// </summary>
public sealed partial class App : Application
{
    // Brand accent for the window / taskbar icon disc.
    private static readonly Color _accent = Color.FromRgb(0x2a, 0x6f, 0xdb);

    private MainWindow? _window;
    private LauncherWindow? _launcher;
    private MainWindowViewModel? _viewModel;
    private AgentConnection? _connection;
    private UiPreferences? _prefs;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    // Top-left of the outgoing surface, carried to the incoming one so a More / Less swap opens in place.
    private PixelPoint? _swapAnchor;

    /// <inheritdoc/>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            // Restore UI preferences before any window shows.
            var prefs = UiPreferences.Load();
            _prefs = prefs;
            RequestedThemeVariant = prefs.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };

            // Apply UI language before the first frame.
            Loc.Instance.ApplyStartupCulture(prefs.Language);

            var connection = new AgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection, prefs);
            _viewModel = viewModel;
            desktop.ShutdownRequested += (_, _) => connection.Dispose();

            // Both surfaces close to the resident tray: the process exits when its last window closes. The
            // More / Less toggle swaps windows opening the next before closing the current, so the window count
            // never reaches zero mid-toggle and the lifetime never shuts the process down.
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;

            // Cold launch from the tray (#187) opens the compact launcher; a direct run opens the full console.
            if (desktop.Args is { } args && Array.IndexOf(args, "--launcher") >= 0)
            {
                ShowLauncher();
            }
            else
            {
                ShowMainWindow();
            }

            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The compact launcher (the "Less" surface): quick connect, More to expand to the console, or close to the tray.
    private void ShowLauncher()
    {
        if (_launcher is not null)
        {
            _launcher.Activate();
            return;
        }

        var launcher = new LauncherWindow
        {
            DataContext = _viewModel,
            Icon = BuildIcon(_accent),
        };
        _launcher = launcher;
        launcher.OpenAppRequested += ExpandToConsole;
        launcher.Closed += (_, _) =>
        {
            if (ReferenceEquals(_launcher, launcher))
            {
                _launcher = null;
            }
        };
        // Collapsing from the console lands the launcher where the console sat; a cold launch keeps CenterScreen.
        ApplyStartupPosition(launcher, _swapAnchor);
        _swapAnchor = null;
        _desktop!.MainWindow = launcher;
        launcher.Show();
    }

    // The full console (the "More" surface): all settings sections; Less collapses back to the launcher. Used for
    // a direct launch and when the launcher's More promotes the process to the console.
    private void ShowMainWindow()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var window = new MainWindow
        {
            DataContext = _viewModel,
            Icon = BuildIcon(_accent),
            Width = _prefs!.Width,
            Height = _prefs.Height,
        };
        _window = window;
        window.CollapseRequested += CollapseToLauncher;
        window.Closing += OnMainWindowClosing;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        // Reopen where it was left: maximized, or at the carried swap anchor / remembered position; else centered.
        if (_prefs.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
        else
        {
            ApplyStartupPosition(window, _swapAnchor ?? RememberedConsolePos());
        }

        _swapAnchor = null;
        _desktop!.MainWindow = window;
        window.Show();
    }

    // Launcher "More": open the console at the launcher's spot, then drop the launcher.
    private void ExpandToConsole()
    {
        _swapAnchor = _launcher?.Position;
        ShowMainWindow();
        _launcher?.Close();
    }

    // Console "Less": reopen the launcher at the console's spot, then drop the console (its Closing persists geometry).
    private void CollapseToLauncher()
    {
        _swapAnchor = _window?.Position;
        ShowLauncher();
        _window?.Close();
    }

    // Persist window geometry and stop the create-form camera as the console closes (both on Less and on a real
    // close to the tray).
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _viewModel?.Config.StopScan();

        if (sender is MainWindow window && _prefs is not null)
        {
            _prefs.Maximized = window.WindowState == WindowState.Maximized;
            // Persist geometry from the normal state only, so a maximized close keeps the last restored bounds.
            if (window.WindowState == WindowState.Normal)
            {
                _prefs.Width = window.ClientSize.Width;
                _prefs.Height = window.ClientSize.Height;
                _prefs.PosX = window.Position.X;
                _prefs.PosY = window.Position.Y;
            }

            _prefs.Save();
        }
    }

    // The remembered console position, or null when unset (first run) so the default centering applies.
    private PixelPoint? RememberedConsolePos()
        => _prefs is null || double.IsNaN(_prefs.PosX) || double.IsNaN(_prefs.PosY)
            ? null
            : new PixelPoint((int)_prefs.PosX, (int)_prefs.PosY);

    // Opens the window at a fixed position (clamped on-screen once measured); leaves CenterScreen when none given.
    private static void ApplyStartupPosition(Window window, PixelPoint? desired)
    {
        if (desired is not { } position)
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = position;
        window.Opened += (_, _) => ClampToScreen(window);
    }

    // Pulls a window fully back into its screen's work area, so a stale saved position can't strand it off-screen.
    private static void ClampToScreen(Window window)
    {
        var screens = window.Screens;
        var screen = screens?.ScreenFromWindow(window) ?? screens?.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var frame = window.FrameSize ?? window.Bounds.Size;
        var width = (int)Math.Ceiling(frame.Width * screen.Scaling);
        var height = (int)Math.Ceiling(frame.Height * screen.Scaling);
        var x = Math.Max(area.X, Math.Min(window.Position.X, area.X + Math.Max(0, area.Width - width)));
        var y = Math.Max(area.Y, Math.Min(window.Position.Y, area.Y + Math.Max(0, area.Height - height)));
        window.Position = new PixelPoint(x, y);
    }

    // Power glyph shared with the big connection button.
    private static readonly Geometry _powerGlyph = Geometry.Parse(
        "M13 3h-2v10h2V3zm4.83 2.17l-1.42 1.42C17.99 7.86 19 9.81 19 12c0 3.87-3.13 7-7 7s-7-3.13-7-7c0-2.19 1.01-4.14 2.58-5.42L6.17 5.17C4.23 6.82 3 9.26 3 12c0 4.97 4.03 9 9 9s9-4.03 9-9c0-2.74-1.23-5.18-3.17-6.83z");

    // Draws the app icon at runtime (white power glyph on the accent disc).
    private static WindowIcon BuildIcon(Color disc)
    {
        var discBrush = new SolidColorBrush(disc);
        using var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawEllipse(discBrush, null, new Point(16, 16), 15.5, 15.5);
            // Scale the 24x24 glyph to ~20px and centre it.
            using (ctx.PushTransform(Matrix.CreateScale(20.0 / 24, 20.0 / 24) * Matrix.CreateTranslation(6, 6)))
            {
                ctx.DrawGeometry(Brushes.White, null, _powerGlyph);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
