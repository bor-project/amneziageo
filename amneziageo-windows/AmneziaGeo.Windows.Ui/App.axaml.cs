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
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
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
            // Closing the window must NOT exit the app — it hides to the tray and the agent keeps the
            // tunnel up in the background. The process shuts down only from the tray "Выход" item (or an OS
            // session end), so the lifetime must not auto-quit when the last window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var connection = new AgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection);
            var window = new MainWindow { DataContext = viewModel, Icon = BuildIcon() };
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
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The close box hides the window to the tray rather than quitting; a real exit goes through ExitApp,
    // which sets _exiting so the genuine close is allowed through.
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
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
            Icon = BuildIcon(),
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

    // Draws the app / tray icon at runtime (a white power glyph on the accent disc) so the project needs no
    // binary icon asset. The render-to-bitmap PNG drives both the tray icon and the window icon.
    private static WindowIcon BuildIcon()
    {
        var accent = new SolidColorBrush(Color.FromRgb(0x2a, 0x6f, 0xdb));
        var stroke = new Pen(Brushes.White, 2.6) { LineCap = PenLineCap.Round };
        using var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawEllipse(accent, null, new Point(16, 16), 15, 15);
            ctx.DrawEllipse(null, stroke, new Point(16, 17), 6, 6);
            // Cut the gap at the top of the ring, then drop the stem through it (the power symbol).
            ctx.DrawRectangle(accent, null, new Rect(13, 6, 6, 7));
            ctx.DrawLine(stroke, new Point(16, 8), new Point(16, 16.5));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
