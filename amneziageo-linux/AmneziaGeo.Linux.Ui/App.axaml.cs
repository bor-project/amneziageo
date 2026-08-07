using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using AmneziaGeo.Linux.Ui.Services;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Desktop;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Linux.Ui;

/// <summary>
/// The Avalonia application.
/// </summary>
public sealed partial class App : Application
{
    private AgentConnection? _connection;

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
            var prefs = UiPreferences.Load();
            RequestedThemeVariant = prefs.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            Loc.Instance.ApplyStartupCulture(prefs.Language);
            DesktopQrScanning.Register();

            var connection = new AgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection, prefs);
            desktop.ShutdownRequested += (_, _) => connection.Dispose();
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;

            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = prefs.Width,
                Height = prefs.Height,
            };
            desktop.MainWindow = window;
            window.Show();
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
