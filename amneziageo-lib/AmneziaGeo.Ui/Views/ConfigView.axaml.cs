using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Controls;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Config screen view.
/// </summary>
internal sealed partial class ConfigView : UserControl
{
    // Окно, с которого читаются клавиши каталога.
    private TopLevel? _keys;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => PushPaneWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => PushPaneWidth(Bounds.Width);
    }

    // Колонки каталога меряются по пане, в которой он стоит, а не по окну вокруг неё.
    private void PushPaneWidth(double width)
    {
        if (DataContext is ConfigViewModel vm)
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
            || DataContext is not ConfigViewModel vm)
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

    // Способы добавления: файл, живой сканер QR и ручной ввод.
    private void OnAddOptions(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm)
        {
            return;
        }

        ConfigAddOptions.Present(sender as Control, this, vm, OnAddSourcePicked);
    }

    // A system picker drops focus on the way back; the name is what the user checks first.
    private void OnAddSourcePicked(ConfigAddSource source)
    {
        if (source is not ConfigAddSource.File)
        {
            return;
        }

        SectionConfigNameBox.Focus(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Unspecified);
    }

    // Способы экспорта: экран QR, ссылка в буфер и файл конфигурации.
    private void OnExportOptions(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm)
        {
            return;
        }

        var options = new List<ActionOption>
        {
            new(
                Loc.Instance.Get("Main_ShowQrButton"),
                Glyphs.Qr,
                () => vm.SelectConfigSectionCommand.Execute("export")),
            new(Loc.Instance.Get("Main_CopyLinkButton"), Glyphs.Link, CopyExportLink),
            new(Loc.Instance.Get("Main_SaveToFileButton"), Glyphs.Download, SaveExportFile),
        };

        ActionOptions.Present(
            sender as Control,
            vm.Sheet,
            Loc.Instance.Get("Main_ExportConfigTitle"),
            Loc.Instance.Get("Main_ExportConfigSubtitle"),
            options);
    }

    // Копирует ссылку vpn:// открытой конфигурации.
    private async void CopyExportLink()
    {
        if (DataContext is not ConfigViewModel { ConfigExport: { } export })
        {
            return;
        }

        export.ShowQrLinkCommand.Execute(null);
        if (await ExportActions.CopyToClipboardAsync(this, export.Payload))
        {
            export.StatusMessage = Loc.Instance.Get("QrCode_CopiedToClipboard");
        }
    }

    // Сохраняет текст конфигурации файлом.
    private async void SaveExportFile()
    {
        if (DataContext is not ConfigViewModel { ConfigExport: { } export })
        {
            return;
        }

        var saved = await ExportActions.SaveTextAsync(
            this,
            export.ConfText,
            Loc.Instance.Get("QrCode_SaveTitle"),
            export.ConfigName + ".conf");
        if (saved)
        {
            export.StatusMessage = Loc.Instance.Get("MainCode_Saved");
        }
    }

    // Copy the export payload (vpn link or .conf text) to the clipboard.
    private async void OnCopyExport(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ExportDialogViewModel vm)
        {
            return;
        }

        if (await ExportActions.CopyToClipboardAsync(this, vm.Payload))
        {
            vm.StatusMessage = Loc.Instance.Get("QrCode_CopiedToClipboard");
        }
    }

    // Save the rendered QR as a PNG.
    private async void OnDownloadQr(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ExportDialogViewModel vm || vm.QrImage is not { } qr)
        {
            return;
        }

        var saved = await ExportActions.SaveBinaryAsync(
            this,
            stream => qr.Save(stream),
            Loc.Instance.Get("QrCode_SaveTitle"),
            vm.ConfigName + ".png",
            "png",
            "PNG");
        if (saved)
        {
            vm.StatusMessage = Loc.Instance.Get("QrCode_Saved");
        }
    }

    // Hands the rendered QR to another application.
    private async void OnSendQr(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ExportDialogViewModel vm || vm.QrImage is not { } qr)
        {
            return;
        }

        await ExportActions.SendImageAsync(qr, vm.ConfigName + ".png");
    }
}
