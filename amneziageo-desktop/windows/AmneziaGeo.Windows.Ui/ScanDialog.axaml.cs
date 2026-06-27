using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AmneziaGeo.Windows.Ui.Services;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Live camera QR scanner: previews the camera and closes with the decoded config on a valid QR.
/// </summary>
public sealed partial class ScanDialog : Window
{
    private QrCameraScanner? _scanner;

    /// <summary>
    /// ctor
    /// </summary>
    public ScanDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not ScanDialogViewModel vm)
        {
            return;
        }

        _scanner = new QrCameraScanner(
            bitmap => Dispatcher.UIThread.Post(() => vm.Preview = bitmap),
            text => Dispatcher.UIThread.Post(() => OnDecoded(vm, text)));
        try
        {
            await _scanner.StartAsync();
        }
        catch (Exception ex)
        {
            vm.StatusMessage = ex.Message;
        }
    }

    private void OnDecoded(ScanDialogViewModel vm, string text)
    {
        var imported = VpnLinkCodec.TryDecodeQr(text);
        if (imported is null)
        {
            vm.StatusMessage = "QR распознан, но это не конфигурация - продолжаю…";
            return;
        }

        vm.Result = imported;
        Close(true);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_scanner is not null)
        {
            await _scanner.DisposeAsync();
            _scanner = null;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
