using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Profile screen view.
/// </summary>
internal sealed partial class ProfileView : UserControl
{
    private readonly HeaderReflow _header;

    /// <summary>
    /// ctor
    /// </summary>
    public ProfileView()
    {
        InitializeComponent();
        _header = new HeaderReflow(HeaderGrid, HeaderTabs, PickerHost, Picker, PickerLabelFloat, PickerLabelInline,
            () => (DataContext as ProfileViewModel)?.IsCompact ?? false);
        DataContextChanged += (_, _) => _header.Apply();
    }

    // Opens the platform per-app split picker for the open profile.
    private void OnAppSplitClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileViewModel { OpenProfile.Name: { Length: > 0 } name })
        {
            AppSplitBridge.Present(name);
        }
    }
}
