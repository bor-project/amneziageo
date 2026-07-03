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

        var path = await PickFileAsync(Loc.Instance.Get("MainCode_QrImageTitle"), "png", "jpg", "jpeg", "bmp");
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
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_ReadyPressSave");
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
        if (string.IsNullOrWhiteSpace(vm.SectionConfigName) && !string.IsNullOrWhiteSpace(imported.Name))
        {
            vm.SectionConfigName = imported.Name!;
        }

        vm.SectionConfigStatus = Loc.Instance.Get("MainCode_QrRecognizedPressSave");
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
            vm.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
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

    // Standalone config-import (the Config settings section): adds a config to the shared catalogue without a
    // profile. The form binds the window VM's SectionConfig* fields, so unlike the per-profile import these
    // handlers read/write the MainWindowViewModel rather than a row's BalancerItemViewModel.
    private async void OnSectionConfigBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var path = await PickFileAsync(Loc.Instance.Get("MainCode_ConfigurationTitle"), "conf");
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
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_ClipboardNoText");
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
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_RecognizedPressSave");
        }
        else
        {
            vm.SectionConfigStatus = Loc.Instance.Get("MainCode_ClipboardNotRecognized");
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

    // "Собрать логи" (#82): ask the agent for a redacted diagnostics zip, then let the user save a copy where
    // they want. The bundle is built agent-side (only SYSTEM can read both processes' logs) under ProgramData,
    // which this UI can read; we copy it to the picked file.
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

    // Builds the per-source action menu (update / delete) and shows it at the given target. The menu is
    // built here, with each command assigned directly from the row's view model, because a MenuItem hosted
    // in a flyout popup does not reliably inherit the row's DataContext for a {Binding}-based command in
    // Avalonia 11. Sources cannot be edited (only added / removed / refreshed), so the menu has no "edit".
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
