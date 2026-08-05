using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui;

/// <summary>
/// Shared home and settings surface hosted by both desktop and mobile applications.
/// Feeds its width to the view-model and lays out the settings columns for the compact / wide split.
/// </summary>
public sealed partial class MainView : UserControl
{
    private MainWindowViewModel? _vm;
    private TopLevel? _topLevel;

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
            _vm.Home.PropertyChanged -= OnHomePropertyChanged;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.WindowWidth = Bounds.Width > 0 ? Bounds.Width : 987;
            _vm.WindowHeight = Bounds.Height > 0 ? Bounds.Height : 610;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Home.PropertyChanged += OnHomePropertyChanged;
            ApplySettingsLayout();
        }
    }

    // A loader stands in for the home content until the first agent snapshot, so the connect control cannot take
    // focus at open; seat it once it appears.
    private void OnHomePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.IsReady) && _vm?.IsHome == true)
        {
            FocusCurrentScreen();
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

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.BackRequested += OnBackRequested;
            _topLevel.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Bubble);
        }

        FocusCurrentScreen();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            _topLevel.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            _topLevel = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    // Escape is taken at the top level: with nothing focused the key event never routes through this view.
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && e.Key is Key.Escape && NavigateBack())
        {
            e.Handled = true;
        }
    }

    // Android back button. Unhandled it finishes the activity, so settings would quit the app instead of
    // stepping back; the system still gets it from home.
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (!e.Handled && NavigateBack())
        {
            e.Handled = true;
        }
    }

    // Escape and the back button take the back arrow's route: section detail first, then home.
    private bool NavigateBack()
    {
        if (_vm is null || !_vm.IsSettings)
        {
            return false;
        }

        _vm.NavBackCommand.Execute(null);
        return true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsCompact)
            or nameof(MainWindowViewModel.SettingsDetailOpen)
            or nameof(MainWindowViewModel.IsSettings))
        {
            ApplySettingsLayout();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsHome)
            or nameof(MainWindowViewModel.IsSettings))
        {
            FocusCurrentScreen();
        }
    }

    // Seats initial D-pad focus on each screen so an Android TV remote always has a starting point:
    // the connect control on home, the active section row in settings. On a television home takes
    // NavigationMethod.Directional so the connect control shows its ring as soon as the screen appears;
    // everywhere else the focus is seated silently, without a ring.
    private void FocusCurrentScreen()
    {
        var vm = _vm;
        if (vm is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_vm != vm)
            {
                return;
            }

            if (vm.IsHome)
            {
                HomePowerButton.Focus(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Unspecified);
            }
            else if (vm.IsSettings)
            {
                var rows = RailMenu.Children.OfType<Button>();
                var target = rows.FirstOrDefault(b => b.Classes.Contains("active")) ?? rows.FirstOrDefault();
                target?.Focus(NavigationMethod.Unspecified);
            }
        }, DispatcherPriority.Loaded);
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
