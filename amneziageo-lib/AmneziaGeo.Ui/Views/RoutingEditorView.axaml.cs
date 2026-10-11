using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Shared routing rule + traffic editor, bound to RoutingViewModel (RoutingEditor / RoutingSettings).
/// </summary>
internal sealed partial class RoutingEditorView : UserControl
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RowPace _pace;
    private bool _feeding;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingEditorView()
    {
        _pace = new RowPace(() => _clock.ElapsedMilliseconds);
        InitializeComponent();
    }

    // The open editor while rules of its shown bucket wait for their rows and it stands on screen.
    private RoutingListEditorViewModel? Opening =>
        (DataContext as RoutingViewModel)?.RoutingEditor is { RowsPending: true } editor && IsEffectivelyVisible ? editor : null;

    // Asks for the next rows of a long bucket once the ones before are laid out.
    private void OnEditorLaidOut(object? sender, EventArgs e)
    {
        _pace.Laid();
        if (_feeding || Opening is null)
        {
            return;
        }

        _feeding = true;
        Dispatcher.UIThread.Post(FeedRows, DispatcherPriority.Background);
    }

    // Builds the next portion of the rows.
    private void FeedRows()
    {
        _feeding = false;
        if (Opening is { } editor)
        {
            _pace.Feed(editor.ShowMoreRows);
        }
    }

    // Copies everything the expanded rule covers to the clipboard.
    private async void OnCopyRuleEntries(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RoutingRuleItemViewModel item })
        {
            return;
        }

        await ExportActions.CopyToClipboardAsync(this, string.Join(Environment.NewLine, item.Entries));
    }

    // Puts the picked path into the add row, where it is added like a typed one. Editor VM resolved via the
    // routing VM's RoutingEditor (MenuFlyout items do not inherit the editor's DataContext).
    private async void OnPickApplication(object? sender, RoutedEventArgs e)
    {
        await PutPickedAsync(() => PickFileAsync(Loc.Instance.Get("MainCode_ApplicationTitle"), Extensions()));
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        await PutPickedAsync(PickFolderAsync);
    }

    private async Task PutPickedAsync(Func<Task<string?>> pick)
    {
        if ((DataContext as RoutingViewModel)?.RoutingEditor is not { } vm)
        {
            return;
        }

        var path = await pick();
        if (!string.IsNullOrEmpty(path))
        {
            vm.RuleInput = path;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Instance.Get("MainCode_AppFolderTitle"),
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    // Suggestions come as a dropdown while the editor is wide enough for one.
    private void OnEditorSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyLayoutWidth(e.NewSize.Width);
    }

    private void OnEditorDataContextChanged(object? sender, EventArgs e)
    {
        ApplyLayoutWidth(Bounds.Width);
    }

    private void ApplyLayoutWidth(double width)
    {
        if ((DataContext as RoutingViewModel)?.RoutingEditor is { } vm)
        {
            vm.IsWideLayout = width >= UiLayout.CompactWidth;
        }
    }

    // What a program file is called: a Linux one carries no extension at all.
    private static string[] Extensions() => OperatingSystem.IsWindows() ? ["exe"] : [];

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
            FileTypeFilter = patterns.Count == 0 ? null : [new FilePickerFileType(title) { Patterns = patterns }],
        };

        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
