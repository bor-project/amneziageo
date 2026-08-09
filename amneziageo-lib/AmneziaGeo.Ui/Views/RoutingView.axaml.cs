using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Routing screen view.
/// </summary>
internal sealed partial class RoutingView : UserControl
{
    private readonly HeaderReflow _header;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingView()
    {
        InitializeComponent();
        _header = new HeaderReflow(HeaderGrid, HeaderTabs, PickerHost, Picker, PickerLabelFloat, PickerLabelInline,
            () => (DataContext as RoutingViewModel)?.IsCompact ?? false);
        DataContextChanged += (_, _) => _header.Apply();
    }

    // Routing-list export: copies the open form - the QR as a picture, the config as text.
    private async void OnRoutingExportCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        if (vm.IsTransferQr)
        {
            if (vm.RoutingQrImage is { } qr && await ExportActions.CopyImageAsync(this, qr, QrName(vm)))
            {
                vm.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
            }

            return;
        }

        if (await ExportActions.CopyToClipboardAsync(this, vm.BuildTransferPayload()))
        {
            vm.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
        }
    }

    // Saves the open form to a file of its own kind.
    private async void OnRoutingExportSave(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        var title = Loc.Instance.Get("MainCode_SaveRoutingListTitle");
        if (vm.IsTransferQr)
        {
            if (vm.RoutingQrImage is { } qr
                && await ExportActions.SaveBinaryAsync(this, stream => qr.Save(stream), title, QrName(vm), "png", "PNG"))
            {
                vm.StatusMessage = Loc.Instance.Get("MainCode_Saved");
            }

            return;
        }

        if (await ExportActions.SaveTextAsync(this, vm.BuildTransferPayload(), title, vm.SuggestedFileName))
        {
            vm.StatusMessage = Loc.Instance.Get("MainCode_Saved");
        }
    }

    // Hands the open form to another application.
    private async void OnRoutingExportSend(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingListEditorViewModel vm })
        {
            return;
        }

        if (vm.IsTransferQr)
        {
            if (vm.RoutingQrImage is { } qr)
            {
                await ExportActions.SendImageAsync(qr, QrName(vm));
            }

            return;
        }

        await ExportActions.SendTextAsync(vm.BuildTransferPayload(), vm.SuggestedFileName);
    }

    // Picture name of the list.
    private static string QrName(RoutingListEditorViewModel vm) => Path.ChangeExtension(vm.SuggestedFileName, "png");

    // Routing-list import: paste from the clipboard / load from a file into the draft editor.
    private async void OnRoutingImportPaste(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutingViewModel vm)
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
            if (vm.RoutingEditor is { } editor)
            {
                editor.StatusMessage = Loc.Instance.Get("MainCode_ClipboardNoText");
            }

            return;
        }

        vm.ApplyImportText(text);
    }

    private async void OnRoutingImportFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutingViewModel vm)
        {
            return;
        }

        var file = await FilePickers.OpenAsync(this, Loc.Instance.Get("MainCode_RoutingListTitle"), "txt");
        if (file is null)
        {
            return;
        }

        try
        {
            // Read through the storage stream, not a local path: an Android picker returns a content:// URI whose
            // TryGetLocalPath is null.
            vm.ApplyImportText(await ReadAllTextAsync(file));
        }
        catch (Exception ex)
        {
            if (vm.RoutingEditor is { } editor)
            {
                editor.StatusMessage = ex.Message;
            }
        }
    }

    private static async Task<string> ReadAllTextAsync(PickedFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
