using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Cold-launch launcher: connect/disconnect the active profile in place, open the full app, or dismiss to the
/// resident tray (#187).
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
        _ = ToggleConnectionAsync();
    }

    // Connect/disconnect the active profile without closing: the window stays up so the user watches the
    // connection progress, then dismisses it via Close when ready.
    private async Task ToggleConnectionAsync()
    {
        if (Vm is null)
        {
            return;
        }

        var command = Vm.Home.ToggleConnectionCommand;
        if (command.CanExecute(null))
        {
            await command.ExecuteAsync(null);
        }
    }

    // "More": hand off to the shell, which opens the console and drops this window.
    private void OnOpenApp(object? sender, RoutedEventArgs e)
    {
        OpenAppRequested?.Invoke();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
