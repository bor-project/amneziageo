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
    /// <summary>
    /// ctor
    /// </summary>
    public WideShell()
    {
        InitializeComponent();
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
