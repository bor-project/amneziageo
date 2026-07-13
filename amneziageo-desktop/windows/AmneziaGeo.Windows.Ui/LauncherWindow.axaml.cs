using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Cold-launch launcher: connect the active profile, open the full app, or dismiss to the resident tray (#187).
/// </summary>
public sealed partial class LauncherWindow : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public LauncherWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the user chooses to open the full application window.
    /// </summary>
    public event Action? OpenAppRequested;

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        _ = ConnectAndCloseAsync();
    }

    private async Task ConnectAndCloseAsync()
    {
        if (Vm is not null)
        {
            var command = Vm.Home.ToggleConnectionCommand;
            if (command.CanExecute(null))
            {
                await command.ExecuteAsync(null);
            }
        }

        Close();
    }

    private void OnOpenApp(object? sender, RoutedEventArgs e)
    {
        OpenAppRequested?.Invoke();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
