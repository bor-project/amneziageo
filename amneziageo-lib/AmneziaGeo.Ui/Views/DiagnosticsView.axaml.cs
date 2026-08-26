using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Diagnostics screen view: the viewer over every source the agent answers from.
/// </summary>
internal sealed partial class DiagnosticsView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public DiagnosticsView()
    {
        InitializeComponent();
    }

    // Builds the diagnostics archive and offers it for saving. The agent writes it under its own account, and a
    // copy the user can reach is what support gets.
    private async void OnCollectArchive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsViewModel vm || await vm.CollectArchiveAsync() is not { } path)
        {
            return;
        }

        try
        {
            await using var archive = File.OpenRead(path);
            if (await ExportActions.SaveBinaryAsync(this, archive.CopyTo,
                    Loc.Instance.Get("Main_ArchiveSaveTitle"), Path.GetFileName(path), "zip", "ZIP"))
            {
                vm.ArchiveStatus = Loc.Instance.Get("Main_ArchiveSaved");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The agent's own directory is not always open to the user; the status line names the archive there.
        }
    }
}
