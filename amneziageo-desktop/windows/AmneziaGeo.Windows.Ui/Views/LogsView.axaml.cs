using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui.Views;

/// <summary>
/// Logs screen view.
/// </summary>
internal sealed partial class LogsView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public LogsView()
    {
        InitializeComponent();
    }

    // Opens the shared logs directory in Explorer, selecting the file picked in the combo when it exists.
    private void OnOpenLogsFolder(object? sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmneziaGeo", "logs");
        var selected = (DataContext as LogsViewModel)?.SelectedLogFile?.Name;
        var file = selected is not null ? Path.Combine(dir, selected) : null;

        try
        {
            if (file is not null && File.Exists(file))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
        }
    }
}
