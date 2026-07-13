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

            // Cold launch from the tray (#187): a lightweight themed launcher instead of the full window. Its
            // buttons connect and quit, open the full app in place, or dismiss to the resident tray.
            if (desktop.Args is { } args && Array.IndexOf(args, "--launcher") >= 0)
            {
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                var launcher = new LauncherWindow
                {
                    DataContext = viewModel,
                    Icon = BuildIcon(_accent),
                };
                launcher.OpenAppRequested += () => ShowMainWindow(desktop, viewModel);
                launcher.Show();
            }
            else
            {
                ShowMainWindow(desktop, viewModel);
            }

            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Builds and shows the full main window, taking over as the shutdown anchor. Used for a normal launch and
    // when the launcher's "open app" promotes the process to the full GUI.
    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, MainWindowViewModel viewModel)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var window = new MainWindow
        {
            DataContext = viewModel,
            Icon = BuildIcon(_accent),
            Width = _prefs!.Width,
            Height = _prefs.Height,
        };
        _window = window;
        window.Closing += OnMainWindowClosing;
        desktop.MainWindow = window;
        // Closing the window fully exits the GUI process; the resident tray holds the tunnel and reopens it.
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    // Persist window size and stop the create-form camera as the GUI exits.
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _viewModel?.Config.StopScan();

        if (_window is not null && _prefs is not null)
        {
            // ClientSize tracks the user's actual resize.
            _prefs.Width = _window.ClientSize.Width;
            _prefs.Height = _window.ClientSize.Height;
            _prefs.Save();
        }
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
