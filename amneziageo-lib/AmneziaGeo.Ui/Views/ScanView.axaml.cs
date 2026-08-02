using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Inline live camera QR scanner.
/// </summary>
internal sealed partial class ScanView : UserControl
{
    private IQrCameraScanner? _scanner;

    /// <summary>
    /// ctor
    /// </summary>
    public ScanView()
    {
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Sync();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _ = StopAsync();
    }

    // Runs the platform camera while bound to a scan model, stops it otherwise.
    private async void Sync()
    {
        if (DataContext is ScanViewModel vm)
        {
            if (_scanner is not null)
            {
                return;
            }

            var scanner = QrCameraScannerHost.Create(
                bitmap => Dispatcher.UIThread.Post(() => vm.Preview = bitmap),
                text => Dispatcher.UIThread.Post(() => vm.ReportRaw(text)));
            if (scanner is null)
            {
                vm.StatusMessage = Loc.Instance.Get("QrScanner_CameraNotFound");
                return;
            }

            _scanner = scanner;
            try
            {
                await scanner.StartAsync();
            }
            catch (Exception ex)
            {
                vm.StatusMessage = ex.Message;
            }
        }
        else
        {
            await StopAsync();
        }
    }

    private async Task StopAsync()
    {
        var scanner = _scanner;
        _scanner = null;
        if (scanner is not null)
        {
            await scanner.DisposeAsync();
        }
    }
}
