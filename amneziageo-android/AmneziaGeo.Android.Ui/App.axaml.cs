using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AmneziaGeo.Android.Ui.ViewModels;
using AmneziaGeo.Android.Ui.Views;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// The Avalonia application.
/// </summary>
public sealed partial class App : Avalonia.Application
{
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
            singleView.MainView = new MainView
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}