using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AmneziaGeo.Ui.Controls;
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

    // Steps from the tabs into the section under them.
    private void OnHeaderTabsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Down)
        {
            return;
        }

        e.Handled = PaneFocus.FocusFirst(PaneFocus.Shown(SettingsSection, ExportSection, ImportPicker, ImportEditor));
    }

    // Returns to the tabs from the section's top row.
    private void OnBodyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Up)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused
            || PaneFocus.HasNeighbour(Body, focused, NavigationDirection.Up))
        {
            return;
        }

        e.Handled = PaneFocus.FocusFirst(HeaderTabs);
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

        // The list exports as a QR picture by default, so the same picture is taken back here (#38).
        var file = await FilePickers.OpenAsync(this, Loc.Instance.Get("MainCode_RoutingListTitle"),
            "txt", "conf", "png", "jpg", "jpeg", "bmp", "gif");
        if (file is null)
        {
            return;
        }

        try
        {
            // Read through the storage stream, not a local path: an Android picker returns a content:// URI whose
            // TryGetLocalPath is null.
            var raw = await file.ReadAllBytesAsync();
            if (!FileContent.LooksLikeImage(raw))
            {
                vm.ApplyImportText(new UTF8Encoding(false, false).GetString(raw).TrimStart('\ufeff'));
                return;
            }

            using var picture = new MemoryStream(raw);
            if (QrCodec.Decode(picture) is not { } scanned)
            {
                if (vm.RoutingEditor is { } withoutCode)
                {
                    withoutCode.StatusMessage = Loc.Instance.Get("MainCode_QrNotFound");
                }

                return;
            }

            vm.ApplyImportText(scanned);
        }
        catch (Exception ex)
        {
            if (vm.RoutingEditor is { } editor)
            {
                editor.StatusMessage = ex.Message;
            }
        }
    }
}
