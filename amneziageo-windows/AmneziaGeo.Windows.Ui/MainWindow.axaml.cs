using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    // After the ✎ command flips the profile header into its inline name editor, move keyboard focus into the
    // box and select its text so the user can type immediately - the editor is otherwise only made visible,
    // not focused. Deferred to Background priority so it runs after the IsVisible binding has applied.
    private void OnBeginProfileNameEdit(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = this.FindControl<TextBox>("ProfileNameBox");
            box?.Focus();
            box?.SelectAll();
        }, DispatcherPriority.Background);
    }

    // Same as OnBeginProfileNameEdit but for the config header's inline name editor.
    private void OnBeginConfigNameEdit(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = this.FindControl<TextBox>("ConfigNameBox");
            box?.Focus();
            box?.SelectAll();
        }, DispatcherPriority.Background);
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
                vm.NewConfigStatus = "В буфере обмена нет картинки - используйте файл или камеру";
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

    // "Вручную": open the large editor seeded with the current draft text; on OK write it back so the
    // normal Save (import) flow picks it up.
    private async void OnNewConfigManual(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BalancerItemViewModel vm })
        {
            return;
        }

        var editor = new ConfigEditorViewModel { Text = vm.NewConfigText };
        var dialog = new ConfigEditorDialog { DataContext = editor };
        if (await dialog.ShowDialog<bool>(this))
        {
            vm.NewConfigText = editor.Text;
            vm.NewConfigStatus = "Готово - нажмите «Сохранить».";
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

        vm.NewConfigStatus = "QR распознан - нажмите «Сохранить»";
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

    // Copy the current export payload (.conf or vpn:// link) to the clipboard. Clipboard access is a
    // window concern, so the inline export pane's button is wired here rather than in the view model.
    private async void OnConfigExportCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { ConfigExport: { } vm })
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(vm.Payload);
            vm.StatusMessage = "Скопировано в буфер обмена.";
        }
    }

    // Save the current export payload to a file the user picks (window concern, like the copy above).
    private async void OnConfigExportSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { ConfigExport: { } vm })
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить конфигурацию",
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.Payload);
        vm.StatusMessage = "Сохранено.";
    }

    // WebSocket settings share (copy / save / paste / load) - mirrors the config export/import. The
    // button's DataContext is the open config's ConfigTransportViewModel (the WS section's DataContext).
    private async void OnWsExportCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigTransportViewModel vm })
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(vm.BuildTransferPayload());
            vm.StatusMessage = "Скопировано в буфер обмена.";
        }
    }

    private async void OnWsExportSave(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigTransportViewModel vm })
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить настройки WebSocket",
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.BuildTransferPayload());
        vm.StatusMessage = "Сохранено.";
    }

    private async void OnWsImportPaste(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigTransportViewModel vm })
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
            vm.StatusMessage = "В буфере обмена нет текста.";
            return;
        }

        vm.ApplyImport(text);
    }

    private async void OnWsImportFile(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConfigTransportViewModel vm })
        {
            return;
        }

        var path = await PickFileAsync("Настройки WebSocket", "txt", "conf");
        if (path is null)
        {
            return;
        }

        try
        {
            vm.ApplyImport(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            vm.StatusMessage = ex.Message;
        }
    }

    // Routing-list share (copy / save / paste / load) - the button's DataContext is the window VM's
    // RoutingEditor (a RoutingListEditorViewModel).
    private async void OnRoutingExportCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(vm.BuildTransferPayload());
            vm.StatusMessage = "Скопировано в буфер обмена.";
        }
    }

    private async void OnRoutingExportSave(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить список маршрутизации",
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.BuildTransferPayload());
        vm.StatusMessage = "Сохранено.";
    }

    // "Показать QR": render the list (name + rules) as a QR in a dialog. On demand, so the editor never holds
    // a live QR that would swap its Image and steal input focus.
    private async void OnRoutingShowQr(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        var dialog = new QrDialog
        {
            DataContext = new QrDialogViewModel("QR списка маршрутизации", vm.BuildTransferPayload(), vm.SuggestedFileName),
        };
        await dialog.ShowDialog(this);
    }

    private async void OnRoutingImportPaste(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
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
            vm.StatusMessage = "В буфере обмена нет текста.";
            return;
        }

        vm.ApplyImport(text);
    }

    private async void OnRoutingImportFile(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        var path = await PickFileAsync("Список маршрутизации", "txt");
        if (path is null)
        {
            return;
        }

        try
        {
            vm.ApplyImport(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            vm.StatusMessage = ex.Message;
        }
    }

    // Profile export/import (the Импорт/экспорт aspect): the whole profile as a portable JSON bundle.
    // Clipboard and file access are window concerns (like the config / WebSocket / routing share above), so
    // these live here; the export/import IPC round-trip belongs to the window VM and this just moves the
    // resulting text to/from the clipboard or a file.
    private async void OnProfileExportCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var json = await vm.ExportOpenProfileAsync();
        if (json is null)
        {
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(json);
            vm.ProfilePortStatus = "Профиль скопирован в буфер обмена.";
        }
    }

    private async void OnProfileExportSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var json = await vm.ExportOpenProfileAsync();
        if (json is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить профиль",
            SuggestedFileName = $"{vm.OpenProfileName}.agprofile.json",
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(json);
        vm.ProfilePortStatus = "Профиль сохранён.";
    }

    private async void OnProfileImportPaste(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
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
            vm.ProfilePortStatus = "В буфере обмена нет текста.";
            return;
        }

        await vm.ImportProfileBundleAsync(text);
    }

    private async void OnProfileImportFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var path = await PickFileAsync("Профиль AmneziaGeo", "json");
        if (path is null)
        {
            return;
        }

        try
        {
            await vm.ImportProfileBundleAsync(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            vm.ProfilePortStatus = ex.Message;
        }
    }

    /// <summary>
    /// Opens the per-source action menu (update / delete) on a right-click of a source row.
    /// </summary>
    private void OnSourceRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SourceItemViewModel source } target)
        {
            return;
        }

        ShowSourceMenu(target, source, atPointer: true);
        e.Handled = true;
    }

    /// <summary>
    /// Opens the same per-source action menu from the row's "⋮" (kebab) button, anchored to the button.
    /// </summary>
    private void OnSourceKebab(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SourceItemViewModel source } button)
        {
            return;
        }

        ShowSourceMenu(button, source, atPointer: false);
        e.Handled = true;
    }

    // Builds the per-source action menu (update / delete) and shows it at the given target. The menu is
    // built here, with each command assigned directly from the row's view model, because a MenuItem hosted
    // in a flyout popup does not reliably inherit the row's DataContext for a {Binding}-based command in
    // Avalonia 11. Sources cannot be edited (only added / removed / refreshed), so the menu has no "edit".
    private static void ShowSourceMenu(Control target, SourceItemViewModel source, bool atPointer)
    {
        var update = new MenuItem
        {
            Header = "Обновить базу",
            Command = source.UpdateCommand,
        };
        var delete = new MenuItem
        {
            Header = "Удалить базу",
            Command = source.RemoveCommand,
        };
        var flyout = new MenuFlyout();
        flyout.Items.Add(update);
        flyout.Items.Add(delete);
        flyout.ShowAt(target, showAtPointer: atPointer);
    }

}
