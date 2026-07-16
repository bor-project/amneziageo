using System;
using Avalonia.Controls;
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

    /// <inheritdoc/>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (DataContext as MainWindowViewModel)?.SelectStartupSection();
    }
}
