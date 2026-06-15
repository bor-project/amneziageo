using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Dialog for editing a configuration's geo split-tunnel settings.
/// </summary>
public sealed partial class ConfigSettingsDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public ConfigSettingsDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is ConfigSettingsViewModel vm)
        {
            await vm.LoadSuggestionsAsync();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigSettingsViewModel vm)
        {
            await vm.SaveAsync();
        }

        Close(true);
    }
}
