using System;

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
        SizeChanged += (_, e) => PushPaneWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => PushPaneWidth(Bounds.Width);
        LayoutUpdated += (_, _) => PushChrome();
    }

    // The card breakpoint is measured against the panel the viewer sits in, not the window around it.
    private void PushPaneWidth(double width)
    {
        if (DataContext is LogsViewModel vm)
        {
            vm.PaneWidth = width;
        }
    }

    // Height everything but the body takes. The section grows by what the body is short of it, so the head is
    // measured rather than counted: it changes with the source, the offers and the width.
    private void PushChrome()
    {
        if (DataContext is not LogsViewModel vm || Bounds.Height <= 0)
        {
            return;
        }

        var chrome = Bounds.Height - BodyFrame.Bounds.Height;
        if (Math.Abs(chrome - vm.ChromeHeight) > 0.5)
        {
            vm.ChromeHeight = chrome;
        }
    }

    // An offered destination fills the target field.
    private void OnPickSuggestion(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogsViewModel vm && (sender as Control)?.DataContext is string value)
        {
            vm.PickSuggestion(value);

            // The list the offer came from closes with it, so the run button takes the ring a remote leaves
            // nowhere else.
            ProbeRunButton.Focus(NavigationMethod.Directional);
        }
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

    // Puts one probe on the clipboard whole: the head, every leg and the verdict.
    private async void OnCopyProbe(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ProbeEntryItem probe)
        {
            return;
        }

        await ExportActions.CopyToClipboardAsync(this, probe.Line);
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

    // Exports the whole selected log table to a text file the user picks. A phone hands back a document and
    // no path at all, so the text goes into the stream the picker opens.
    private async void OnExportLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm)
        {
            return;
        }

        if (await vm.BuildExportTextAsync() is { } text)
        {
            await ExportActions.SaveTextAsync(this, text, string.Empty, vm.SelectedLogType + ".log");
        }
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
