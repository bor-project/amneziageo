using System.ComponentModel;
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
    // Brand accent - the static window / taskbar icon disc (the big power button's look, on-brand colour).
    private static readonly Color _accent = Color.FromRgb(0x2a, 0x6f, 0xdb);

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private MainWindowViewModel? _viewModel;
    private AgentConnection? _connection;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayOpen;
    private NativeMenuItem? _trayExit;
    private UiPreferences? _prefs;
    private bool _exiting;

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

            // Restore per-user UI preferences (#51) before any window shows, so the saved theme and window
            // size apply with no light->dark / resize flicker.
            var prefs = UiPreferences.Load();
            _prefs = prefs;
            RequestedThemeVariant = prefs.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

            // Apply the UI language (#106) before any window is built, so the first frame is already in the
            // chosen culture: saved preference -> system UI language -> English. A later switch is live.
            Loc.Instance.ApplyStartupCulture(prefs.Language);

            // Don't auto-quit when the window closes: the close box always hides to the tray (the app
            // keeps running in the background - agent link and any active tunnel stay up), and the tray
            // "Выход" exits explicitly. Either way the lifetime must not quit on its own when the last
            // window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var connection = new AgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection, prefs);
            _viewModel = viewModel;
            var window = new MainWindow
            {
                DataContext = viewModel,
                Icon = BuildIcon(_accent),
                Width = prefs.Width,
                Height = prefs.Height,
            };
            _window = window;
            desktop.MainWindow = window;

            window.Closing += OnMainWindowClosing;
            // A genuine lifetime shutdown (OS logoff / our own ExitApp) must be allowed through: mark
            // exiting so the close box handler stops cancelling the close, and release the agent link.
            desktop.ShutdownRequested += (_, _) =>
            {
                _exiting = true;
                connection.Dispose();
            };

            SetUpTrayIcon();
            // Recolour the tray icon whenever the connection state changes (blue/amber/grey).
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The close box (X) always hides the window to the tray so the app keeps running in the background
    // (the agent link and any active tunnel stay up) regardless of VPN state; exit happens only through
    // the tray "Выход", which goes via ExitApp and sets _exiting so the genuine close is allowed through.
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Persist the final window size (#51) on every close - whether hiding to the tray or a genuine
        // exit. (Theme and the selected settings section are saved as they change in the VM.)
        if (_window is not null && _prefs is not null)
        {
            // ClientSize tracks the user's actual resize (Window.Width does not reliably write back); for an
            // Avalonia top-level it equals the value set via Width, so save/restore stays consistent.
            _prefs.Width = _window.ClientSize.Width;
            _prefs.Height = _window.ClientSize.Height;
            _prefs.Save();
        }

        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.TrayStatusColor) && _trayIcon is not null && _viewModel is not null)
        {
            _trayIcon.Icon = BuildIcon(_viewModel.TrayStatusColor);
        }
    }

    private void SetUpTrayIcon()
    {
        var open = new NativeMenuItem(Loc.Instance.Get("Tray_Open"));
        open.Click += (_, _) => ShowMainWindow();
        var exit = new NativeMenuItem(Loc.Instance.Get("Tray_Exit"));
        exit.Click += (_, _) => ExitApp();
        _trayOpen = open;
        _trayExit = exit;

        var menu = new NativeMenu();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);

        _trayIcon = new TrayIcon
        {
            // Starts grey (disconnected); OnViewModelPropertyChanged recolours it as state moves.
            Icon = BuildIcon(_viewModel?.TrayStatusColor ?? _accent),
            ToolTipText = "AmneziaGeo",
            Menu = menu,
            IsVisible = true,
        };
        // Left-click on the tray icon restores the window.
        _trayIcon.Clicked += (_, _) => ShowMainWindow();

        // The tray items are native (not XAML bindings), so re-label them on a live language switch.
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged()
    {
        if (_trayOpen is not null)
        {
            _trayOpen.Header = Loc.Instance.Get("Tray_Open");
        }

        if (_trayExit is not null)
        {
            _trayExit.Header = Loc.Instance.Get("Tray_Exit");
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _connection?.Dispose();
        _desktop?.Shutdown();
    }

    // The "power" glyph of the big on-screen connection button (AgPowerGeometry - a 24x24 Material path),
    // shared so the icon matches the control.
    private static readonly Geometry _powerGlyph = Geometry.Parse(
        "M13 3h-2v10h2V3zm4.83 2.17l-1.42 1.42C17.99 7.86 19 9.81 19 12c0 3.87-3.13 7-7 7s-7-3.13-7-7c0-2.19 1.01-4.14 2.58-5.42L6.17 5.17C4.23 6.82 3 9.26 3 12c0 4.97 4.03 9 9 9s9-4.03 9-9c0-2.74-1.23-5.18-3.17-6.83z");

    // Draws the app / tray icon at runtime (a white power glyph on a coloured disc) so the project needs no
    // binary icon asset. The render-to-bitmap PNG drives both the tray icon (disc tinted by connection
    // state) and the window icon (brand accent). The glyph is the same one the on-screen big button uses.
    private static WindowIcon BuildIcon(Color disc)
    {
        var discBrush = new SolidColorBrush(disc);
        using var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawEllipse(discBrush, null, new Point(16, 16), 15.5, 15.5);
            // Scale the 24x24 glyph to ~20px and centre it in the 32px icon.
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
