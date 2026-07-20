using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Inline selective bundle import: paste or load a bundle JSON and import it.
/// </summary>
internal sealed partial class BundleImportView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public BundleImportView()
    {
        InitializeComponent();
    }

    private async void OnLoadFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleImportViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
}
