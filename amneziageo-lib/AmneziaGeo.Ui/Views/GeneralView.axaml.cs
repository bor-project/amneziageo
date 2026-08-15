using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// General screen view.
/// </summary>
internal sealed partial class GeneralView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public GeneralView()
    {
        InitializeComponent();
    }

    // Puts one address on the clipboard, so it can be pasted into the client that has to use it.
    private async void OnCopyProxyEndpoint(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ProxyEndpointRow row })
        {
            await ExportActions.CopyToClipboardAsync(this, row.Value);
        }
    }
}
