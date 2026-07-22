using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Shared routing rule + traffic editor, bound to RoutingViewModel (RoutingEditor / RoutingSettings).
/// </summary>
internal sealed partial class RoutingEditorView : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public RoutingEditorView()
    {
        InitializeComponent();
    }

    // Per-app tunneling source picks. Editor VM resolved via the routing VM's RoutingEditor (MenuFlyout items
    // do not inherit the editor's DataContext).
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
