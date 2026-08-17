using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Logs screen view.
/// </summary>
internal sealed partial class LogsView : UserControl
{
    // Both phases and handled events too: the key is only watched, never taken from whoever wants it.
    private const RoutingStrategies Both = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;

    private TopLevel? _topLevel;

    /// <summary>
    /// ctor
    /// </summary>
    public LogsView()
    {
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null)
        {
            return;
        }

        _topLevel.AddHandler(KeyDownEvent, OnGlobalKeyDown, Both, handledEventsToo: true);
        _topLevel.AddHandler(KeyUpEvent, OnGlobalKeyUp, Both, handledEventsToo: true);
        if (_topLevel is Window window)
        {
            window.Deactivated += OnWindowDeactivated;
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(KeyDownEvent, OnGlobalKeyDown);
            _topLevel.RemoveHandler(KeyUpEvent, OnGlobalKeyUp);
            if (_topLevel is Window window)
            {
                window.Deactivated -= OnWindowDeactivated;
            }

            _topLevel = null;
        }

        Freeze(false);
        base.OnDetachedFromVisualTree(e);
    }

    // Held Ctrl stops the viewer taking new rows: a body replaced under the pointer loses the selection being made.
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl || e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Freeze(true);
        }
    }

    private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            Freeze(false);
        }
    }

    // A window left with the key down never sees it come up.
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        Freeze(false);
    }

    private void Freeze(bool frozen)
    {
        if (DataContext is LogsViewModel vm)
        {
            vm.IsFrozen = frozen;
        }
    }

    // Puts one log row on the clipboard whole: time, level and text.
    private async void OnCopyEntry(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not LogEntryItem entry)
        {
            return;
        }

        await ExportActions.CopyToClipboardAsync(this, entry.Line);
    }

    // Puts one destination on the clipboard: the address, its name, where it goes and what it holds.
    private async void OnCopyRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not LiveRowItem row)
        {
            return;
        }

        await ExportActions.CopyToClipboardAsync(this, row.Line);
    }

    // Puts what the viewer shows on the clipboard whole.
    private async void OnCopyBody(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm)
        {
            return;
        }

        await ExportActions.CopyToClipboardAsync(this, vm.VisibleText());
    }

    // Exports the whole selected log table to a text file the user picks; the agent writes it.
    private async void OnExportLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm)
        {
            return;
        }

        var path = await ExportActions.PickSavePathAsync(
            this,
            string.Empty,
            vm.SelectedLogType + ".log",
            "log",
            "Log");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await vm.ExportToAsync(path);
    }

    // Hands the whole selected log table to another application.
    private async void OnSendLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm)
        {
            return;
        }

        if (await vm.BuildExportTextAsync() is { } text)
        {
            await ExportActions.SendTextAsync(text, vm.SelectedLogType + ".log");
        }
    }
}
