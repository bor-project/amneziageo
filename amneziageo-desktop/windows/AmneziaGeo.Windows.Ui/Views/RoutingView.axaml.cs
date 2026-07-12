using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui.Views;

/// <summary>
/// Routing screen view.
/// </summary>
internal sealed partial class RoutingView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public RoutingView()
    {
        InitializeComponent();
    }

    // Autosave the open list when focus leaves one of its fields.
    private void OnRoutingFieldBlur(object? sender, RoutedEventArgs e)
    {
        (DataContext as RoutingViewModel)?.AutoSaveOnBlur();
    }

    // Routing-list share (copy / save / paste / load).
    private async void OnRoutingExportCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(vm.BuildTransferPayload());
            vm.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
        }
    }

    private async void OnRoutingExportSave(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm } || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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

    private async void OnRoutingImportPaste(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
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
        if ((DataContext as RoutingViewModel)?.RoutingEditor is { } vm)
        {
            await vm.EnterRunningModeAsync();
        }
    }

    private async void OnAppSourceInstalled(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as RoutingViewModel)?.RoutingEditor is { } vm)
        {
            await vm.EnterInstalledModeAsync();
        }
    }

    private async void OnAppSourceFolder(object? sender, RoutedEventArgs e)
    {
        if ((DataContext as RoutingViewModel)?.RoutingEditor is not { } vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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
        if ((DataContext as RoutingViewModel)?.RoutingEditor is not { } vm)
        {
            return;
        }

        var path = await PickFileAsync(Loc.Instance.Get("MainCode_ApplicationTitle"), "exe");
        if (!string.IsNullOrEmpty(path))
        {
            vm.AddAppToken($"app:path={path}");
        }
    }

    private async Task<string?> PickFileAsync(string title, params string[] extensions)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var patterns = extensions.Select(ext => $"*.{ext}").ToList();
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        };

        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
