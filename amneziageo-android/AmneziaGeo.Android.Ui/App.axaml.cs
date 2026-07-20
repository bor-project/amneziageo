using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using AmneziaGeo.Android.Ui.Services;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using SharedMainView = AmneziaGeo.Ui.MainView;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// The Avalonia application.
/// </summary>
public sealed partial class App : Avalonia.Application
{
    private AndroidAgentConnection? _connection;

    /// <inheritdoc/>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var prefs = UiPreferences.Load();
            RequestedThemeVariant = prefs.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            Loc.Instance.ApplyStartupCulture(prefs.Language);

            var connection = new AndroidAgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection, prefs);
            singleView.MainView = new SharedMainView
            {
                DataContext = viewModel,
            };
            viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
