using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    /// <summary>
    /// ctor
    /// </summary>
    public ConfigView()
    {
        InitializeComponent();
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

    // Кладёт текст открытой конфигурации в буфер, не раскрывая его на экране.
    private async void OnCopyConfText(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel { ConfigExport: { } export })
        {
            return;
        }

        if (await ExportActions.CopyToClipboardAsync(this, export.ConfText))
        {
            export.StatusMessage = Loc.Instance.Get("QrCode_CopiedToClipboard");
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
