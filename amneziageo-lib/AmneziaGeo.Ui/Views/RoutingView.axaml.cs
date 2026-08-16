using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    /// <summary>
    /// ctor
    /// </summary>
    public RoutingView()
    {
        InitializeComponent();
    }

    // Returns to the header from the section's top row.
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

        e.Handled = PaneFocus.FocusFirst(HeaderActions);
    }

    // Способы добавления: файл, буфер обмена, живой сканер QR и пустой список.
    private void OnAddOptions(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutingViewModel vm)
        {
            return;
        }

        RoutingAddOptions.Present(sender as Control, this, vm);
    }

    // Способы экспорта: экран QR, текст списка в буфер и файл.
    private void OnExportOptions(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutingViewModel vm)
        {
            return;
        }

        var options = new List<ActionOption>
        {
            new(
                Loc.Instance.Get("Main_ShowQrButton"),
                Glyphs.Qr,
                () => vm.SelectRoutingSectionCommand.Execute("export")),
            new(Loc.Instance.Get("Main_CopyButton"), Glyphs.Copy, CopyListPayload),
            new(Loc.Instance.Get("Main_SaveToFileButton"), Glyphs.Download, SaveListPayload),
        };

        ActionOptions.Present(
            sender as Control,
            vm.Sheet,
            Loc.Instance.Get("Main_ExportListTitle"),
            Loc.Instance.Get("Main_TransferSubtitle"),
            options);
    }

    // Копирует текст открытого списка.
    private async void CopyListPayload()
    {
        if (DataContext is not RoutingViewModel { RoutingEditor: { } editor })
        {
            return;
        }

        if (await ExportActions.CopyToClipboardAsync(this, editor.BuildTransferPayload()))
        {
            editor.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
        }
    }

    // Сохраняет текст открытого списка файлом.
    private async void SaveListPayload()
    {
        if (DataContext is not RoutingViewModel { RoutingEditor: { } editor })
        {
            return;
        }

        var saved = await ExportActions.SaveTextAsync(
            this,
            editor.BuildTransferPayload(),
            Loc.Instance.Get("MainCode_SaveRoutingListTitle"),
            editor.SuggestedFileName);
        if (saved)
        {
            editor.StatusMessage = Loc.Instance.Get("MainCode_Saved");
        }
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

}
