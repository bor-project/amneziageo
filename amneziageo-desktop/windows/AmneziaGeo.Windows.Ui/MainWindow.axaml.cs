using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// The configuration console window.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the user collapses the console back to the compact launcher ("Less").
    /// </summary>
    public event Action? CollapseRequested;

    /// <inheritdoc/>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (DataContext as MainWindowViewModel)?.SelectStartupSection();
    }

    private void OnCollapse(object? sender, RoutedEventArgs e)
    {
        CollapseRequested?.Invoke();
    }
}
