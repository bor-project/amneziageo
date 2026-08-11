using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Diagnostics check pane: the channel ladder and the targeted check, with the verdict they produced.
/// </summary>
internal sealed partial class CheckView : UserControl
{
    // The window the keys are read from: Ctrl reaches the pane wherever the focus sits.
    private TopLevel? _keys;

    /// <summary>
    /// ctor
    /// </summary>
    public CheckView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keys = TopLevel.GetTopLevel(this);
        _keys?.AddHandler(KeyDownEvent, OnKeyPressed, RoutingStrategies.Tunnel, true);
        _keys?.AddHandler(KeyUpEvent, OnKeyReleased, RoutingStrategies.Tunnel, true);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keys?.RemoveHandler(KeyDownEvent, OnKeyPressed);
        _keys?.RemoveHandler(KeyUpEvent, OnKeyReleased);
        _keys = null;
        Release();
        base.OnDetachedFromVisualTree(e);
    }

    // Holds the live rows still while Ctrl is down, and copies the run on Ctrl+C when nothing is selected.
    private void OnKeyPressed(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CheckViewModel vm || !vm.IsActive)
        {
            return;
        }

        vm.LivePaused = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is Key.LeftCtrl or Key.RightCtrl;
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Report.SelectedText.Length == 0)
        {
            OnCopyReport(sender, e);
            e.Handled = true;
        }
    }

    private void OnKeyReleased(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Release();
        }
    }

    // Lets the live rows move again.
    private void Release()
    {
        if (DataContext is CheckViewModel vm)
        {
            vm.LivePaused = false;
        }
    }

    // Puts the selected rows, or the whole run, on the clipboard.
    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CheckViewModel vm)
        {
            return;
        }

        var text = Report.SelectedText.Length > 0 ? Report.SelectedText : vm.BuildReportText();
        if (await ExportActions.CopyToClipboardAsync(this, text))
        {
            vm.StatusMessage = Loc.Instance.Get("MainCode_CopiedToClipboard");
        }
    }
}
