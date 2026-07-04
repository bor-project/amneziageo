using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AmneziaGeo.Windows.Ui.Services;
using AmneziaGeo.Windows.Ui.ViewModels;
using AmneziaGeo.Localization;

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

    // Config section import form: «Камера» scans a QR live.
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
            if (vm.SectionConfigNameIsDefault && !string.IsNullOrWhiteSpace(scan.Result.Name))
            {
                vm.SectionConfigName = scan.Result.Name!;
            }

            vm.SectionConfigStatus = string.Empty; // clear any stale failure from a prior pick.
        }
    }

    // «Редактировать»: open the large editor seeded with the current text; on OK write it back.
    private async void OnSectionConfigEdit(object? sender, RoutedEventArgs e)
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
            var imported = VpnLinkCodec.TryDecode(editor.Text);
            if (imported is not null)
            {
                // Adopt the config's embedded name over the generic default.
                if (vm.SectionConfigNameIsDefault && !string.IsNullOrWhiteSpace(imported.Name))
                {
                    vm.SectionConfigName = imported.Name!;
                }

                vm.SectionConfigStatus = string.Empty;
            }
            else
            {
                vm.SectionConfigStatus = string.IsNullOrWhiteSpace(editor.Text)
                    ? string.Empty
                    : Loc.Instance.Get("MainVm_ConfigNotRecognized");
            }
        }
    }

    private static void ApplyQrToSectionConfig(MainWindowViewModel vm, Bitmap bitmap)
    {
        var text = QrCodec.Decode(bitmap);
        if (text is null)
        {
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_QrNotFound");
            return;
        }

        var imported = VpnLinkCodec.TryDecodeQr(text);
        if (imported is null)
        {
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_QrNotConfig");
            return;
        }

        vm.SectionConfigText = imported.ConfText;
        if (vm.SectionConfigNameIsDefault && !string.IsNullOrWhiteSpace(imported.Name))
        {
            vm.SectionConfigName = imported.Name!;
        }

        // No status: a recognised QR auto-saves and the form closes.
        vm.SectionConfigStatus = string.Empty;
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

    // Copy the export payload to the clipboard (window concern).
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
            vm.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
        }
    }

    // Save the export payload to a picked file.
    private async void OnConfigExportSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { ConfigExport: { } vm })
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.Instance.Get("MainCode_SaveConfigTitle"),
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.Payload);
        vm.StatusMessage = Loc.Instance.Get("MainCode_Saved");
    }

    // WebSocket settings share (copy / save / paste / load).
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
            vm.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
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
            Title = Loc.Instance.Get("MainCode_SaveWebSocketTitle"),
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.BuildTransferPayload());
        vm.StatusMessage = Loc.Instance.Get("MainCode_Saved");
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
            vm.StatusMessage = Loc.Instance.Get("MainCode_ClipboardNoText");
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

        var path = await PickFileAsync(Loc.Instance.Get("MainCode_WebSocketSettingsTitle"), "txt", "conf");
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

    // Routing-list share (copy / save / paste / load).
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
            vm.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
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
            Title = Loc.Instance.Get("MainCode_SaveRoutingListTitle"),
            SuggestedFileName = vm.SuggestedFileName,
        });
        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(vm.BuildTransferPayload());
        vm.StatusMessage = Loc.Instance.Get("MainCode_Saved");
    }

    // «Показать QR»: render the list as a QR on demand.
    private async void OnRoutingShowQr(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        var dialog = new QrDialog
        {
            DataContext = new QrDialogViewModel(Loc.Instance.Get("MainCode_RoutingListQrTitle"), vm.BuildTransferPayload(), vm.SuggestedFileName),
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
            vm.StatusMessage = Loc.Instance.Get("MainCode_ClipboardNoText");
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

        var path = await PickFileAsync(Loc.Instance.Get("MainCode_RoutingListTitle"), "txt");
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

    // Per-app tunneling source picks. Editor VM resolved via the window VM's RoutingEditor (MenuFlyout
    // items do not inherit the editor's DataContext).
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
            Title = Loc.Instance.Get("MainCode_AppFolderTitle"),
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

        var path = await PickFileAsync(Loc.Instance.Get("MainCode_ApplicationTitle"), "exe");
        if (!string.IsNullOrEmpty(path))
        {
            vm.AddAppToken($"app:path={path}");
        }
    }

    // Standalone config-import: adds a config to the shared catalogue without a profile.
    private async void OnSectionConfigBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // One «Файл» picker for both a config text file and a QR image; extension decides which.
        var path = await PickFileAsync(Loc.Instance.Get("MainCode_ConfigurationTitle"),
            "conf", "txt", "vpn", "png", "jpg", "jpeg", "bmp", "gif");
        if (path is null)
        {
            return;
        }

        try
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext is "png" or "jpg" or "jpeg" or "bmp" or "gif")
            {
                using var bitmap = new Bitmap(path);
                ApplyQrToSectionConfig(vm, bitmap);
                return;
            }

            var raw = File.ReadAllText(path);
            if (VpnLinkCodec.TryDecode(raw) is not { } imported)
            {
                vm.SectionConfigText = raw;
                vm.SectionConfigStatus = Loc.Instance.Get("MainVm_ConfigNotRecognized");
                return;
            }

            vm.SectionConfigText = imported.ConfText;
            vm.SectionConfigStatus = string.Empty;
            if (vm.SectionConfigNameIsDefault)
            {
                var name = !string.IsNullOrWhiteSpace(imported.Name)
                    ? imported.Name!
                    : Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    vm.SectionConfigName = name;
                }
            }
        }
        catch (Exception ex)
        {
            vm.SectionConfigStatus = ex.Message;
        }
    }

    // Selective export/import: «Экспорт…» / «Импорт…» buttons.
    private async void OnOpenBundleExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var dialogVm = new BundleExportDialogViewModel(vm.Connection, vm.Balancers, vm.Configs, vm.RoutingLists);
        await dialogVm.LoadRoutingRulesAsync();
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

    // «Собрать логи»: ask the agent for a redacted diagnostics zip, then save a copy.
    private async void OnCollectLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var sourcePath = await vm.RequestDiagnosticsAsync();
        if (sourcePath is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.Instance.Get("MainCode_SaveLogsForSupportTitle"),
            SuggestedFileName = Path.GetFileName(sourcePath),
            FileTypeChoices = [new FilePickerFileType(Loc.Instance.Get("MainCode_ZipArchive")) { Patterns = ["*.zip"] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var input = File.OpenRead(sourcePath);
            await using var output = await file.OpenWriteAsync();
            await input.CopyToAsync(output);
            vm.ShowTransientNotice(Loc.Instance.Get("MainCode_LogsCollectedAndSaved"));
        }
        catch (Exception ex)
        {
            vm.ShowTransientNotice(Loc.Instance.Get("MainCode_LogsSaveFailed", ex.Message));
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

    // Builds the per-source action menu (update / delete). Commands assigned directly from the row's VM
    // because flyout MenuItems do not reliably inherit the row's DataContext in Avalonia 11.
    private static void ShowSourceMenu(Control target, SourceItemViewModel source, bool atPointer)
    {
        var update = new MenuItem
        {
            Header = Loc.Instance.Get("MainCode_UpdateDatabase"),
            Command = source.UpdateCommand,
        };
        var delete = new MenuItem
        {
            Header = Loc.Instance.Get("MainCode_DeleteDatabase"),
            Command = source.RemoveCommand,
        };
        var flyout = new MenuFlyout();
        flyout.Items.Add(update);
        flyout.Items.Add(delete);
        flyout.ShowAt(target, showAtPointer: atPointer);
    }
}
