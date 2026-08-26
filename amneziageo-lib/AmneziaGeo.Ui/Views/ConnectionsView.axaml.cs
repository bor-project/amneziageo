using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Connections screen view.
/// </summary>
internal sealed partial class ConnectionsView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public ConnectionsView()
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
