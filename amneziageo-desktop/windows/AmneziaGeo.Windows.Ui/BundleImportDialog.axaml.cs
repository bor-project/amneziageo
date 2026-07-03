using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.ViewModels;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Modal dialog for the selective bundle import (#91): paste or load a bundle JSON (as produced by
/// <see cref="BundleExportDialog"/>) and import it. No tree preview - the agent's automatic dedup-by-rename
/// conflict policy makes one low-value, the same convention as the existing single-profile import.
/// </summary>
public sealed partial class BundleImportDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public BundleImportDialog()
    {
        InitializeComponent();
    }

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleImportDialogViewModel vm)
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.StatusMessage = Loc.Instance.Get("BundleImportCode_ClipboardEmpty");
            return;
        }

        vm.Payload = text;
        vm.StatusMessage = string.Empty;
    }

    private async void OnLoadFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleImportDialogViewModel vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Instance.Get("BundleImportCode_BundleFileTitle"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }

        try
        {
            vm.Payload = await File.ReadAllTextAsync(path);
            vm.StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            vm.StatusMessage = ex.Message;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
