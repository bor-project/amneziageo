using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

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

    // Exports the whole selected log table to a text file the user picks; the agent writes it.
    private async void OnExportLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = vm.SelectedLogType + ".log",
            DefaultExtension = "log",
            FileTypeChoices = [new FilePickerFileType("Log") { Patterns = ["*.log", "*.txt"] }],
        });
        if (file is null)
        {
            return;
        }

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await vm.ExportToAsync(path);
    }
}
