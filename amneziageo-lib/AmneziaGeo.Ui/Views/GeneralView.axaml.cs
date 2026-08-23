using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
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

    // Builds the diagnostics archive and offers it for saving. The agent writes it under its own account, and a
    // copy the user can reach is what support gets.
    private async void OnCollectDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GeneralViewModel vm || await vm.CollectDiagnosticsAsync() is not { } path)
        {
            return;
        }

        try
        {
            await using var archive = File.OpenRead(path);
            if (await ExportActions.SaveBinaryAsync(this, archive.CopyTo,
                    Loc.Instance.Get("General_DiagnosticsSaveTitle"), Path.GetFileName(path), "zip", "ZIP"))
            {
                vm.DiagnosticsStatus = Loc.Instance.Get("General_DiagnosticsSaved");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The agent's own directory is not always open to the user; the status line names the archive there.
        }
    }
}
