using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    // Окно, с которого читаются клавиши каталога.
    private TopLevel? _keys;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => PushPaneWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => PushPaneWidth(Bounds.Width);
    }

    // Колонки каталога меряются по пане, в которой он стоит, а не по окну вокруг неё.
    private void PushPaneWidth(double width)
    {
        if (DataContext is RoutingViewModel vm)
        {
            vm.PaneWidth = width;
        }
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keys = TopLevel.GetTopLevel(this);
        _keys?.AddHandler(KeyDownEvent, OnCatalogKey, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keys?.RemoveHandler(KeyDownEvent, OnCatalogKey);
        _keys = null;
        base.OnDetachedFromVisualTree(e);
    }

    // Ctrl со стрелкой двигает отмеченную карточку: тело карточки фокус вне телевизора не берёт,
    // и клавиша читается с окна. Погружением, а не всплытием: на всплытии стрелку раньше забирает
    // навигация по фокусу, а модификатора она не различает.
    private void OnCatalogKey(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || !IsEffectivelyVisible
            || DataContext is not RoutingViewModel vm)
        {
            return;
        }

        // В поле ввода Ctrl со стрелкой - шаг по словам, жест туда не лезет.
        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            return;
        }

        var step = CardGesture.Step(e.Key, vm.CatalogColumns);
        if (step == 0)
        {
            return;
        }

        var to = vm.MovePicked(step);
        if (to < 0)
        {
            return;
        }

        e.Handled = true;
        Show(CardStack.IsEffectivelyVisible ? CardStack : CardGrid, to);
    }

    // Ведёт пану за переехавшей карточкой.
    private static void Show(ItemsControl list, int index)
    {
        Dispatcher.UIThread.Post(
            () => (list.ContainerFromIndex(index) as Control)?.BringIntoView(),
            DispatcherPriority.Loaded);
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

        e.Handled = PaneFocus.FocusFirst(CatalogueHeader);
    }

    // Способы добавления: файл, буфер обмена, живой сканер QR и пустой список.
    private void OnAddOptions(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutingViewModel vm)
        {
            return;
        }

        var options = new List<ActionOption>
        {
            new(Loc.Instance.Get("Main_FileButton"), Glyphs.File, ImportFromFile),
            new(Loc.Instance.Get("Main_PasteButton"), Glyphs.Paste, ImportFromClipboard),
        };
        if (vm.CameraScanAvailable)
        {
            options.Add(new ActionOption(
                Loc.Instance.Get("Main_CameraButton"),
                Glyphs.Qr,
                () => vm.BeginCameraImportCommand.Execute(null)));
        }

        options.Add(new ActionOption(
            Loc.Instance.Get("Main_CreateManuallyButton"),
            Glyphs.Pencil,
            () => vm.BeginManualImportCommand.Execute(null)));

        ActionOptions.Present(
            sender as Control,
            vm.Sheet,
            Loc.Instance.Get("Main_AddListTitle"),
            string.Empty,
            options);
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

    // Routing-list import: paste from the clipboard into a fresh draft.
    private async void ImportFromClipboard()
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
            vm.BeginImportDraft();
            if (vm.RoutingEditor is { } editor)
            {
                editor.StatusMessage = Loc.Instance.Get("MainCode_ClipboardNoText");
            }

            return;
        }

        vm.BeginImportDraft();
        vm.ApplyImportText(text);
    }

    // Routing-list import: load from a file into a fresh draft. Отменённый выбор черновик не открывает.
    private async void ImportFromFile()
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

        vm.BeginImportDraft();
        try
        {
            // Read through the storage stream, not a local path: an Android picker returns a content:// URI whose
            // TryGetLocalPath is null.
            var raw = await file.ReadAllBytesAsync();
            if (!FileContent.LooksLikeImage(raw))
            {
                vm.ApplyImportText(new UTF8Encoding(false, false).GetString(raw).TrimStart('﻿'));
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
