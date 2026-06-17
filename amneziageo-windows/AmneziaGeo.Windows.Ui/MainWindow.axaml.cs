using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.Services;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// The main window.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnNewConfigBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BalancerItemViewModel vm })
        {
            return;
        }

        var path = await PickFileAsync("Конфигурация", "conf");
        if (path is null)
        {
            return;
        }

        try
        {
            vm.NewConfigText = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(vm.NewConfigName))
            {
                vm.NewConfigName = Path.GetFileNameWithoutExtension(path);
            }
        }
        catch (Exception ex)
        {
            vm.NewConfigStatus = ex.Message;
        }
    }

    private async void OnNewConfigQrImage(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BalancerItemViewModel vm })
        {
            return;
        }

        var path = await PickFileAsync("QR-картинка", "png", "jpg", "jpeg", "bmp");
        if (path is null)
        {
            return;
        }

        try
        {
            using var bitmap = new Bitmap(path);
            ApplyQrToNewConfig(vm, bitmap);
        }
        catch (Exception ex)
        {
            vm.NewConfigStatus = ex.Message;
        }
    }

    private async void OnNewConfigCamera(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BalancerItemViewModel vm })
        {
            return;
        }

        var scan = new ScanDialogViewModel();
        var dialog = new ScanDialog { DataContext = scan };
        var ok = await dialog.ShowDialog<bool>(this);
        if (ok && scan.Result is not null)
        {
            vm.NewConfigText = scan.Result.ConfText;
            if (string.IsNullOrWhiteSpace(vm.NewConfigName) && !string.IsNullOrWhiteSpace(scan.Result.Name))
            {
                vm.NewConfigName = scan.Result.Name!;
            }
        }
    }

    private async void OnNewConfigClipboard(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BalancerItemViewModel vm })
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
#pragma warning disable CS0618
            var formats = await clipboard.GetFormatsAsync();
            byte[]? bytes = null;
            foreach (var format in new[] { "PNG", "image/png", "public.png", "image/bmp", "Bitmap", "DeviceIndependentBitmap" })
            {
                if (formats.Contains(format) && await clipboard.GetDataAsync(format) is byte[] data && data.Length > 0)
                {
                    bytes = data;
                    break;
                }
            }
#pragma warning restore CS0618

            if (bytes is null)
            {
                vm.NewConfigStatus = "В буфере обмена нет картинки — используйте файл или камеру";
                return;
            }

            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            ApplyQrToNewConfig(vm, bitmap);
        }
        catch (Exception ex)
        {
            vm.NewConfigStatus = ex.Message;
        }
    }

    private static void ApplyQrToNewConfig(BalancerItemViewModel vm, Bitmap bitmap)
    {
        var text = QrCodec.Decode(bitmap);
        if (text is null)
        {
            vm.NewConfigStatus = "QR-код не найден на картинке";
            return;
        }

        var imported = VpnLinkCodec.TryDecodeQr(text);
        if (imported is null)
        {
            vm.NewConfigStatus = "QR распознан, но это не конфигурация";
            return;
        }

        vm.NewConfigText = imported.ConfText;
        if (string.IsNullOrWhiteSpace(vm.NewConfigName) && !string.IsNullOrWhiteSpace(imported.Name))
        {
            vm.NewConfigName = imported.Name!;
        }

        vm.NewConfigStatus = "QR распознан — нажмите «Сохранить»";
    }

    private async System.Threading.Tasks.Task<string?> PickFileAsync(string title, params string[] extensions)
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

    /// <summary>
    /// Opens the per-config context menu (export) on a right-click of a member row. The menu is built here,
    /// with its command wired from the captured config name, because a MenuItem hosted in a flyout popup does
    /// not reliably inherit the row's DataContext for a {Binding}-based command in Avalonia 11.
    /// </summary>
    private void OnMemberRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: string configName } target)
        {
            return;
        }

        var export = new MenuItem { Header = "Экспорт конфига" };
        export.Click += (_, _) => _ = ExportConfigAsync(configName);
        var flyout = new MenuFlyout();
        flyout.Items.Add(export);
        flyout.ShowAt(target, showAtPointer: true);
        e.Handled = true;
    }

    private async System.Threading.Tasks.Task ExportConfigAsync(string configName)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var export = new ExportDialogViewModel(vm.Connection, configName);
        var dialog = new ExportDialog { DataContext = export };
        _ = export.LoadAsync();
        await dialog.ShowDialog(this);
    }

    private async void OnConfigTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigItemViewModel config })
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialog = new ConfigSettingsDialog
        {
            DataContext = new ConfigSettingsViewModel(vm.Connection, config.Name, config.GeoSplit, config.Rules),
        };
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Opens the per-source context menu (delete) on a right-click of a source row. The menu is built
    /// here, with the command assigned directly from the row's view model, because a MenuItem hosted in a
    /// flyout popup does not reliably inherit the row's DataContext for a {Binding}-based command in
    /// Avalonia 11.
    /// </summary>
    private void OnSourceRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SourceItemViewModel source } target)
        {
            return;
        }

        var delete = new MenuItem
        {
            Header = "Удалить базу",
            Command = source.RemoveCommand,
        };
        var flyout = new MenuFlyout();
        flyout.Items.Add(delete);
        flyout.ShowAt(target, showAtPointer: true);
        e.Handled = true;
    }

}
