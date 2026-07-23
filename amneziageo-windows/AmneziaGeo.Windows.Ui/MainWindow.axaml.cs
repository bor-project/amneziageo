using System;
using Avalonia.Controls;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Desktop window hosting the shared application surface.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
    }

    // Снимает UIPI-фильтр drag-and-drop с окна.
    private void OnWindowOpened(object? sender, EventArgs e) => DragDropFilter.Allow(this);
}
