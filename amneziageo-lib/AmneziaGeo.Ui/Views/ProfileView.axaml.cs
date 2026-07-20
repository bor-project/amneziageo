using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Profile screen view.
/// </summary>
internal sealed partial class ProfileView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public ProfileView()
    {
        InitializeComponent();
    }

    // Autosave the open profile's rename when focus leaves the name field.
    private void OnProfileFieldBlur(object? sender, RoutedEventArgs e)
    {
        (DataContext as ProfileViewModel)?.AutoSaveOnBlur();
    }
}
