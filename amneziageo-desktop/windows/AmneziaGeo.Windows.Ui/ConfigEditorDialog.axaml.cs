using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Modal editor for a config's wg-quick text. Closes with true if saved, false if cancelled.
/// </summary>
public sealed partial class ConfigEditorDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public ConfigEditorDialog()
    {
        InitializeComponent();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigEditorViewModel vm)
        {
            Close(false);
            return;
        }

        if (await vm.SaveAsync())
        {
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
