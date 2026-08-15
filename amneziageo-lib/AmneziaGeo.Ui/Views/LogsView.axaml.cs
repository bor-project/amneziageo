using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

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

    // Puts one log row on the clipboard whole: time, level and text.
    private async void OnCopyEntry(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not LogEntryItem entry)
        {
            return;
        }

        await ExportActions.CopyToClipboardAsync(this, entry.Line);
    }

    // Exports the whole selected log table to a text file the user picks; the agent writes it.
    private async void OnExportLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm)
        {
            return;
        }

        var path = await ExportActions.PickSavePathAsync(
            this,
            string.Empty,
            vm.SelectedLogType + ".log",
            "log",
            "Log");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await vm.ExportToAsync(path);
    }

    // Hands the whole selected log table to another application.
    private async void OnSendLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm)
        {
            return;
        }

        if (await vm.BuildExportTextAsync() is { } text)
        {
            await ExportActions.SendTextAsync(text, vm.SelectedLogType + ".log");
        }
    }
}
