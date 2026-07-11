using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AmneziaGeo.Windows.Ui.Views;

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
    }

    // Toggle masking of the access-token field.
    private void OnToggleTokenReveal(object? sender, RoutedEventArgs e)
    {
        TokenBox.RevealPassword = !TokenBox.RevealPassword;
    }
}
