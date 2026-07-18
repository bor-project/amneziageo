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
    private MainWindowViewModel? _viewModel;
    private AgentConnection? _connection;
    private UiPreferences? _prefs;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    // Guards the self-update against a second trigger launching a concurrent installer.
    private bool _updating;

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

            // Single-instance guarantees this is the only UI process, so a leftover partial download is orphaned
            // from an interrupted run and safe to drop before anything can start a new one (#21).
            GeneralViewModel.CleanupOrphanedPartial();

            var connection = new AgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection, prefs);
            _viewModel = viewModel;
            // Run a download from any surface under the process-alive pin, so closing the window mid-download neither
            // quits the app nor aborts the download (#21).
            viewModel.General.SetPinnedDownloadRunner(RunDownload);
            desktop.ShutdownRequested += (_, _) => connection.Dispose();

            // The window closes to the resident tray: the process exits when it closes.
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;

            // A later launch surfaces this instance (tray click) or asks it to update (tray menu / balloon).
            // Armed only now: the waits post to Dispatcher.UIThread, which must not be touched before Avalonia
            // has bound its platform dispatcher.
            SingleInstance.ActivationHandler = OnActivation;
            SingleInstance.UpdateHandler = OnDownloadRequested;
            SingleInstance.ApplyHandler = OnApplyRequested;
            SingleInstance.StartListening();

            var args = desktop.Args ?? [];
            if (Array.IndexOf(args, "--update") >= 0)
            {
                // Background download (tray balloon / menu "Download update"): fetch the setup with no window,
                // then exit - the tray announces "ready to install". Opening the window reveals the progress.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                viewModel.Start();
                StartHeadlessDownload();
            }
            else if (Array.IndexOf(args, "--apply") >= 0)
            {
                // Background install (tray balloon / menu "Install update"): verify and launch the setup the
                // agent reports as downloaded, with no window.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                viewModel.Start();
                StartHeadlessApply();
            }
            else
            {
                // Direct run or a tray open: the single window opens on Home.
                viewModel.CurrentSurface = "settings";
                ShowMainWindow();
                viewModel.Start();
                // Check for updates on open and once an hour; a found update surfaces as the floating banner (#22).
                viewModel.General.BeginAutoUpdateChecks();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Fresh background-download worker: no window; exits when the download finishes (the tray announces it).
    private void StartHeadlessDownload()
    {
        _viewModel!.CurrentSurface = "none";
        RunDownload();
    }

    // Fresh background-install worker: no window; exits after launching the installer.
    private void StartHeadlessApply()
    {
        _viewModel!.CurrentSurface = "none";
        RunApply();
    }

    // A running instance was asked (tray menu / balloon) to download the update: start it in place. The open
    // window shows its progress.
    private void OnDownloadRequested()
    {
        RunDownload();
    }

    // A running instance was asked (tray menu / balloon) to install the downloaded update: start it in place.
    private void OnApplyRequested()
    {
        RunApply();
    }

    // The window closed while an update operation is still running: the user dismissed the UI, so nothing
    // reopens after it finishes. Only reachable under the update pin - a close otherwise ends the process.
    private void DemoteSurfaceIfDismissed()
    {
        if (_updating && _window is null && _viewModel is not null)
        {
            _viewModel.CurrentSurface = "none";
        }
    }

    // Runs the background download once: pinned alive for the whole fetch, so closing the window cannot abort it.
    // A windowless success exits the worker (the tray announces "ready to install"); a failure or an open window
    // hands back to the normal close-to-tray lifetime, surfacing the window with the status so a user-requested
    // download never ends in silence.
    private async void RunDownload()
    {
        if (_updating || _desktop is null || _viewModel is null)
        {
            return;
        }

        _updating = true;
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            await _viewModel.General.RunBackgroundDownloadAsync();
        }
        finally
        {
            _updating = false;
            // A windowless worker exits on a completed download (the tray announces "ready to install") or when
            // the download was cancelled by an exit relay - surfacing the window there would only fight the exit.
            // Otherwise (a failure, or a cancel with the window open) hand back to the close-to-tray lifetime.
            if (_window is null && (_viewModel.General.UpdateDownloaded || _viewModel.General.WasDownloadCancelledByHost))
            {
                _desktop.Shutdown();
            }
            else
            {
                _desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                if (_window is null)
                {
                    ResurfaceAfterUpdate();
                }
            }
        }
    }

    // Runs the background install once: pinned so a window close cannot abort it. ApplyUpdate exits the process
    // on a successful launch (InstallerLaunched); this is reached otherwise (no file / failed verification), so
    // it hands an open window back to the normal lifetime or surfaces the window with the status.
    private async void RunApply()
    {
        if (_updating || _desktop is null || _viewModel is null)
        {
            return;
        }

        _updating = true;
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            await _viewModel.General.RunApplyDownloadedAsync();
        }
        finally
        {
            _updating = false;
            if (!_viewModel.General.InstallerLaunched)
            {
                _desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                if (_window is null)
                {
                    ResurfaceAfterUpdate();
                }
            }
        }
    }

    // A later launch (tray click) nudges this instance: surface the window on Home, or reveal it when a
    // background update is running windowless. The tray replaces the old connect launcher, so a summon lands on
    // the home connect screen.
    private void OnActivation()
    {
        if (_window is not null)
        {
            _viewModel?.NavHomeCommand.Execute(null);
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Show();
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.CurrentSurface = "settings";
        }

        ShowMainWindow();
    }

    // A windowless update worker ended without installing: bring up the interactive window so the outcome is not
    // lost. Land on the settings General section (the update status line lives there) and arm the auto-checks the
    // headless launch never started (#22).
    private void ResurfaceAfterUpdate()
    {
        _viewModel?.ShowUpdateStatus();
        ShowMainWindow();
        _viewModel?.General.BeginAutoUpdateChecks();
    }

    // The single window: home connect screen + settings console. A direct launch and a tray open both land here.
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
        window.Closing += OnMainWindowClosing;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }

            DemoteSurfaceIfDismissed();
        };
        // Reopen where it was left: maximized, or at the remembered position; else centered.
        if (_prefs.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
        else
        {
            ApplyStartupPosition(window, RememberedConsolePos());
        }

        _desktop!.MainWindow = window;
        window.Show();
    }

    // Persist window geometry and stop the create-form camera as the window closes.
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

    // The remembered window position, or null when unset (first run) so the default centering applies.
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
