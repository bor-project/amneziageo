using System;
using System.ComponentModel;
using Avalonia.Controls;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui;

/// <summary>
/// Shared home and settings surface hosted by both desktop and mobile applications.
/// Feeds its width to the view-model and lays out the settings columns for the compact / wide split.
/// </summary>
public sealed partial class MainView : UserControl
{
    private MainWindowViewModel? _vm;

    /// <summary>
    /// ctor
    /// </summary>
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnViewSizeChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.WindowWidth = Bounds.Width > 0 ? Bounds.Width : 987;
            _vm.WindowHeight = Bounds.Height > 0 ? Bounds.Height : 610;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplySettingsLayout();
        }
    }

    private void OnViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.WindowWidth = e.NewSize.Width;
            _vm.WindowHeight = e.NewSize.Height;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsCompact)
            or nameof(MainWindowViewModel.SettingsDetailOpen)
            or nameof(MainWindowViewModel.IsSettings))
        {
            ApplySettingsLayout();
        }
    }

    // Sizes the rail / splitter / content columns for the current mode: side by side when wide, a single
    // full-width column (rail or content) when compact. Star columns do not collapse when hidden, so the widths
    // are set here rather than by visibility alone.
    private void ApplySettingsLayout()
    {
        if (_vm is null)
        {
            return;
        }

        var cols = SettingsBody.ColumnDefinitions;
        if (!_vm.IsCompact)
        {
            cols[0].MinWidth = 210;
            cols[0].MaxWidth = 320;
            cols[0].Width = new GridLength(260);
            cols[1].Width = GridLength.Auto;
            cols[2].MinWidth = 440;
            cols[2].Width = new GridLength(1, GridUnitType.Star);
        }
        else if (_vm.SettingsDetailOpen)
        {
            cols[0].MinWidth = 0;
            cols[0].MaxWidth = double.PositiveInfinity;
            cols[0].Width = new GridLength(0);
            cols[1].Width = new GridLength(0);
            cols[2].MinWidth = 0;
            cols[2].Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            cols[0].MinWidth = 0;
            cols[0].MaxWidth = double.PositiveInfinity;
            cols[0].Width = new GridLength(1, GridUnitType.Star);
            cols[1].Width = new GridLength(0);
            cols[2].MinWidth = 0;
            cols[2].Width = new GridLength(0);
        }
    }
}
