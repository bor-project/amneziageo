using System;
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

    // Hands the project page to the system browser.
    private async void OnOpenProjectPage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralViewModel vm || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }

        await launcher.LaunchUriAsync(new Uri(vm.ProjectUrl));
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
