using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Dialog to add a configuration or balancer, or restore a backup.
/// </summary>
public sealed partial class AddDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public AddDialog()
    {
        InitializeComponent();
    }

    private async void OnBrowseConfig(object? sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync("Конфигурация", "conf");
        if (file is not null && DataContext is AddDialogViewModel vm)
        {
            vm.ConfigPath = file;
            if (string.IsNullOrWhiteSpace(vm.ConfigName))
            {
                vm.ConfigName = Path.GetFileNameWithoutExtension(file);
            }
        }
    }

    private async void OnBrowseRestore(object? sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync("Бэкап", "agbackup", "zip");
        if (file is not null && DataContext is AddDialogViewModel vm)
        {
            vm.RestorePath = file;
        }
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddDialogViewModel vm)
        {
            return;
        }

        if (vm.SelectedTabIndex == 2)
        {
            if (vm.TryStartRestore())
            {
                Close(true);
            }

            return;
        }

        if (await vm.ConfirmAsync())
        {
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        var patterns = extensions.Select(ext => $"*.{ext}").ToList();
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        };

        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
