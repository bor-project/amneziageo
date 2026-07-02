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

    // After the ✎ command flips the config header into its inline name editor, move keyboard focus into the
    // box and select its text so the user can type immediately - the editor is otherwise only made visible,
    // not focused. Deferred to Background priority so it runs after the IsVisible binding has applied.
    private void OnBeginConfigNameEdit(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = this.FindControl<TextBox>("ConfigNameBox");
            box?.Focus();
            box?.SelectAll();
        }, DispatcherPriority.Background);
    }

    // The Config section's import form uses the window VM's SectionConfig* fields. The file / clipboard picks
    // live further down (OnSectionConfigBrowse / OnSectionConfigClipboard); QR-image, camera and manual-edit
    // mirror them here so the section importer has the same rich options the old inline profile form had.
    private async void OnSectionConfigQrImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
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
            ApplyQrToSectionConfig(vm, bitmap);
        }
        catch (Exception ex)
        {
            vm.SectionConfigStatus = ex.Message;
        }
    }

    private async void OnSectionConfigCamera(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var scan = new ScanDialogViewModel();
        var dialog = new ScanDialog { DataContext = scan };
        var ok = await dialog.ShowDialog<bool>(this);
        if (ok && scan.Result is not null)
        {
            vm.SectionConfigText = scan.Result.ConfText;
            if (string.IsNullOrWhiteSpace(vm.SectionConfigName) && !string.IsNullOrWhiteSpace(scan.Result.Name))
            {
                vm.SectionConfigName = scan.Result.Name!;
            }
        }
    }

    // "Вручную": open the large editor seeded with the current draft text; on OK write it back so the
    // normal Save (import) flow picks it up.
    private async void OnSectionConfigManual(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var editor = new ConfigEditorViewModel { Text = vm.SectionConfigText };
        var dialog = new ConfigEditorDialog { DataContext = editor };
        if (await dialog.ShowDialog<bool>(this))
        {
            vm.SectionConfigText = editor.Text;
            vm.SectionConfigStatus = "Готово - нажмите «Сохранить».";
        }
    }

    private static void ApplyQrToSectionConfig(MainWindowViewModel vm, Bitmap bitmap)
    {
        var text = QrCodec.Decode(bitmap);
        if (text is null)
        {
            vm.SectionConfigStatus = "QR-код не найден на картинке";
            return;
        }

        var imported = VpnLinkCodec.TryDecodeQr(text);
        if (imported is null)
        {
            vm.SectionConfigStatus = "QR распознан, но это не конфигурация";
            return;
        }

        vm.SectionConfigText = imported.ConfText;
        if (string.IsNullOrWhiteSpace(vm.SectionConfigName) && !string.IsNullOrWhiteSpace(imported.Name))
        {
            vm.SectionConfigName = imported.Name!;
        }

        vm.SectionConfigStatus = "QR распознан - нажмите «Сохранить»";
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

    // Per-app tunneling source picks (#68). The editor VM is resolved via the window VM's RoutingEditor
    // because a MenuFlyout item lives in its own visual tree and does not inherit the editor's DataContext.
    private async void OnAppSourceRunning(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as MainWindowViewModel)?.RoutingEditor is { } vm)
        {
            await vm.EnterRunningModeAsync();
        }
    }

    private async void OnAppSourceInstalled(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as MainWindowViewModel)?.RoutingEditor is { } vm)
        {
            await vm.EnterInstalledModeAsync();
        }
    }

    private async void OnAppSourceFolder(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as MainWindowViewModel)?.RoutingEditor is not { } vm)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка приложения",
            AllowMultiple = false,
        });
        var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            vm.AddAppToken($"app:dir={path}");
        }
    }

    private async void OnAppSourceFile(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as MainWindowViewModel)?.RoutingEditor is not { } vm)
        {
            return;
        }

        var path = await PickFileAsync("Приложение", "exe");
        if (!string.IsNullOrEmpty(path))
        {
            vm.AddAppToken($"app:path={path}");
        }
    }

    // Standalone config-import (the Config settings section): adds a config to the shared catalogue without a
    // profile. The form binds the window VM's SectionConfig* fields, so unlike the per-profile import these
    // handlers read/write the MainWindowViewModel rather than a row's BalancerItemViewModel.
    private async void OnSectionConfigBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
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
            vm.SectionConfigText = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(vm.SectionConfigName))
            {
                vm.SectionConfigName = Path.GetFileNameWithoutExtension(path);
            }
        }
        catch (Exception ex)
        {
            vm.SectionConfigStatus = ex.Message;
        }
    }

    // The Config settings section's catalogue combo lists ConfigItemViewModel rows but the window VM keys the
    // open config by NAME (OpenConfig is a string), so selection is wired here instead of a SelectedItem
    // binding (which would be a type mismatch under compiled bindings). Picking a row opens that config.
    private void OnConfigCatalogueSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is ComboBox { SelectedItem: ConfigItemViewModel config })
        {
            vm.OpenConfig = config.Name;
        }
    }

    private async void OnSectionConfigClipboard(object? sender, RoutedEventArgs e)
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
            vm.SectionConfigStatus = "В буфере обмена нет текста.";
            return;
        }

        var imported = VpnLinkCodec.TryDecode(text);
        if (imported is not null)
        {
            vm.SectionConfigText = imported.ConfText;
            if (string.IsNullOrWhiteSpace(vm.SectionConfigName) && !string.IsNullOrWhiteSpace(imported.Name))
            {
                vm.SectionConfigName = imported.Name!;
            }
            vm.SectionConfigStatus = "Распознано — нажмите «Сохранить».";
        }
        else
        {
            vm.SectionConfigStatus = "Текст в буфере не распознан как конфигурация (.conf, vpn://, JSON).";
        }
    }

    // Selective export/import (#91): the General settings section's "Экспорт…" / "Импорт…" buttons. The
    // export dialog is built straight from the window VM's already-loaded snapshot collections - opening it
    // needs no extra IPC round trip - plus the connection it uses for the eventual export/import call.
    private async void OnOpenBundleExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialogVm = new BundleExportDialogViewModel(vm.Connection, vm.Balancers, vm.Configs, vm.RoutingLists);
        var dialog = new BundleExportDialog { DataContext = dialogVm };
        await dialog.ShowDialog(this);
    }

    private async void OnOpenBundleImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialog = new BundleImportDialog { DataContext = new BundleImportDialogViewModel(vm.Connection) };
        await dialog.ShowDialog(this);
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
