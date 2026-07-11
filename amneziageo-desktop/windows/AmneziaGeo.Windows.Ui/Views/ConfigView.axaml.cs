using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui.Views;

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

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    // Config section import form: «Камера» scans a QR live.
    private async void OnSectionConfigCamera(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm || Owner is not { } owner)
        {
            return;
        }

        var scan = new ScanDialogViewModel();
        var dialog = new ScanDialog { DataContext = scan };
        var ok = await dialog.ShowDialog<bool>(owner);
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
        if (DataContext is not ConfigViewModel vm || Owner is not { } owner)
        {
            return;
        }

        var editor = new ConfigEditorViewModel { Text = vm.SectionConfigText };
        var dialog = new ConfigEditorDialog { DataContext = editor };
        if (await dialog.ShowDialog<bool>(owner))
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

    // Standalone config-import: adds a config to the shared catalogue without a profile.
    private async void OnSectionConfigBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm)
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

    private static void ApplyQrToSectionConfig(ConfigViewModel vm, Bitmap bitmap)
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
