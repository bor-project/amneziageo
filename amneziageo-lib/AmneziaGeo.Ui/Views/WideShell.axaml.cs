using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Ui.Controls;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Широкая раскладка: разделы вкладками в шапке, каталог списком слева, его настройки справа.
/// </summary>
internal sealed partial class WideShell : UserControl
{
    // Просвет между счётчиком и соседями в шапке.
    private const double HeaderLinkGap = 16;

    // Ширина счётчика, замеренная, пока он стоял в шапке: убранный своей ширины не отдаёт.
    private double _linkWidth;

    /// <summary>
    /// ctor
    /// </summary>
    public WideShell()
    {
        InitializeComponent();
        LinkFit.SizeChanged += (_, _) => ApplyHeaderFit(HeaderBar.Bounds.Width);
    }

    // Шапка сменила ширину - счётчик скорости либо помещается в неё целиком, либо уходит.
    private void OnHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyHeaderFit(e.NewSize.Width);
    }

    // Вкладки и кнопка подключения берут своё, счётчик занимает остаток.
    private void ApplyHeaderFit(double width)
    {
        if (LinkFit.DesiredSize.Width > 0)
        {
            _linkWidth = LinkFit.DesiredSize.Width;
        }

        var taken = HomeBack.DesiredSize.Width + Tabs.DesiredSize.Width + ConnectPart.DesiredSize.Width;
        LinkFit.IsVisible = width - taken >= _linkWidth + HeaderLinkGap;
    }

    // Способы добавления конфигурации - тот же набор, что на кнопке узкой раскладки.
    private void OnAddServer(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        ConfigAddOptions.Present(sender as Control, this, vm.Config, _ => vm.OpenConfigImport(false));
    }

    // Способы добавления списка маршрутизации.
    private void OnAddRoutingList(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        RoutingAddOptions.Present(sender as Control, this, vm.Routing);
    }
}
