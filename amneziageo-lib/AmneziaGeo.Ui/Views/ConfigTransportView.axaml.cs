using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Config transport editor view.
/// </summary>
internal sealed partial class ConfigTransportView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public ConfigTransportView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => ApplyWidth(Bounds.Width);
    }

    // The pane is narrower than the window, so the row layout follows this view, not the shell flag.
    private void ApplyWidth(double width)
    {
        if (DataContext is ConfigTransportViewModel vm)
        {
            vm.IsCompact = width < UiLayout.FieldRowWidth;
        }
    }

    // Toggle masking of the access-token field.
    private void OnToggleTokenReveal(object? sender, RoutedEventArgs e)
    {
        TokenBox.RevealPassword = !TokenBox.RevealPassword;
    }
}
