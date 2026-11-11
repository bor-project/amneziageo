using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Config transport editor view.
/// </summary>
internal sealed partial class ConfigTransportView : UserControl
{
    private ConfigTransportViewModel? _proxyModel;
    /// <summary>
    /// ctor
    /// </summary>
    public ConfigTransportView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => BindProxyModel();
        AttachedToVisualTree += (_, _) => BindProxyModel();
        DetachedFromVisualTree += (_, _) => UnbindProxyModel();
    }

    private void UnbindProxyModel()
    {
        if (_proxyModel is not null)
        {
            _proxyModel.ProxyReturnRequested -= OnProxyReturn;
            _proxyModel = null;
        }
    }

    private void BindProxyModel()
    {
        UnbindProxyModel();
        _proxyModel = DataContext as ConfigTransportViewModel;
        if (_proxyModel is not null)
        {
            _proxyModel.ProxyReturnRequested += OnProxyReturn;
        }

        ApplyWidth(Bounds.Width);
    }

    private void OnProxyReturn(bool running) => Dispatcher.UIThread.Post(() =>
    {
        var target = running ? ViewRunningProxy : ViewSelectedProxy;
        if (target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
        {
            target.BringIntoView();
            target.Focus(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Tab);
        }
    }, DispatcherPriority.Loaded);

    // The pane is narrower than the window, so the row layout follows this view, not the shell flag.
    private void ApplyWidth(double width)
    {
        if (DataContext is ConfigTransportViewModel vm)
        {
            vm.IsCompact = width < UiLayout.FieldRowWidth;
        }
    }

}
