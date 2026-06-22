using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AmneziaGeo.Windows.Ui.Services;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// The Avalonia application.
/// </summary>
public sealed partial class App : Application
{
    // Brand accent — the static window / taskbar icon disc (the big power button's look, on-brand colour).
    private static readonly Color _accent = Color.FromRgb(0x2a, 0x6f, 0xdb);

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private MainWindowViewModel? _viewModel;
    private AgentConnection? _connection;
    private TrayIcon? _trayIcon;
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
            // Don't auto-quit when the window closes: the close box decides whether to hide to the tray
            // (tunnel up — keep it running in the background) or exit (tunnel off — see OnMainWindowClosing),
            // and the tray "Выход" exits explicitly. Either way the lifetime must not quit on its own when
            // the last window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var connection = new AgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection);
            _viewModel = viewModel;
            var window = new MainWindow { DataContext = viewModel, Icon = BuildIcon(_accent) };
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
            // Recolour the tray icon whenever the connection state changes (green/amber/grey).
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The close box (X) behaves by VPN state: while a tunnel is up or coming up the window hides to the
    // tray and the agent keeps the tunnel running in the background; while it is off or coming down there
    // is nothing to keep alive, so the app exits fully instead of idling in the tray. A real exit goes
    // through ExitApp, which sets _exiting so the genuine close is allowed through.
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        // IsTunnelActive is the desired-tunnel intent: true for connected/connecting, false for
        // disconnecting/disconnected — exactly the "keep in tray vs close fully" split.
        if (_viewModel?.IsTunnelActive == true)
        {
            e.Cancel = true;
            _window?.Hide();
            return;
        }

        // VPN off / coming down: cancel the bare close and run an orderly shutdown (ExitApp calls
        // Shutdown, which closes the window again with _exiting set so it goes through).
        e.Cancel = true;
        ExitApp();
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
        var open = new NativeMenuItem("Открыть");
        open.Click += (_, _) => ShowMainWindow();
        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => ExitApp();

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

    // The "power" glyph of the big on-screen connection button (AgPowerGeometry — a 24x24 Material path),
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
