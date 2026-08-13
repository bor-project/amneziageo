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
            _vm.Sheet.PropertyChanged -= OnSheetPropertyChanged;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.WindowWidth = Bounds.Width > 0 ? Bounds.Width : 987;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Home.PropertyChanged += OnHomePropertyChanged;
            _vm.Sheet.PropertyChanged += OnSheetPropertyChanged;
            ApplySettingsLayout();
        }
    }

    // Пульту и клавиатуре нужна точка входа в шторку: иначе стрелки ходят по экрану за ней.
    private void OnSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ActionSheetViewModel.IsOpen) || _vm?.Sheet.IsOpen != true)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => Controls.PaneFocus.FocusFirst(SheetOptions),
            DispatcherPriority.Loaded);
    }

    // Нажатие мимо карточки убирает шторку.
    private void OnSheetScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            _vm?.Sheet.CloseCommand.Execute(null);
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

    // The back arrow. With the on-screen keyboard up the press only drops it and the screen stays: a
    // multiline field spends Enter on a line break, leaving no other way to put the keyboard away.
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (SoftInputBridge.Dismiss())
        {
            return;
        }

        _vm?.NavBackCommand.Execute(null);
    }

    // Escape and the back button take the back arrow's route: out of a card the remote stepped into, then
    // the section detail, then home.
    private bool NavigateBack()
    {
        if (_vm?.Sheet.IsOpen == true)
        {
            _vm.Sheet.CloseCommand.Execute(null);
            return true;
        }

        if (_vm?.ShowDeleteAsk == true)
        {
            _vm.CancelDeleteAskCommand.Execute(null);
            return true;
        }

        if (Controls.ServerCard.LeaveEntered(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
        {
            return true;
        }

        if (_vm is null)
        {
            return false;
        }

        // The server screen steps back to the connect one; home itself hands the key to the system.
        if (!_vm.IsSettings)
        {
            if (!_vm.IsHomeServers)
            {
                return false;
            }

            _vm.SelectHomeTabCommand.Execute("main");
            return true;
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

        // Пульту нужна точка входа в вопрос: иначе стрелки ходят по карточкам за плашкой.
        if (e.PropertyName is nameof(MainWindowViewModel.ShowDeleteAsk) && _vm?.ShowDeleteAsk == true)
        {
            Dispatcher.UIThread.Post(
                () => DeleteAskCancel.Focus(NavigationMethod.Directional),
                DispatcherPriority.Loaded);
        }
    }

    // Seats initial D-pad focus on each screen so an Android TV remote always has a starting point:
    // the connect control on home, the active section row in settings. On a television both take
    // NavigationMethod.Directional, so the ring marks the entry point as soon as the screen appears.
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
                // The server screen hides the round button, so the way back off it takes the ring instead.
                var connect = vm.ShowHomeConnect ? (Control)HomePowerButton : HomeServersBack;
                connect.Focus(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Unspecified);
            }
            else if (vm.IsSettings)
            {
                FocusRail(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Unspecified);
            }
        }, DispatcherPriority.Loaded);
    }

    // Puts the focus on the open section row.
    private bool FocusRail(NavigationMethod method)
    {
        var rows = RailMenu.Children.OfType<Button>();
        var target = rows.FirstOrDefault(b => b.Classes.Contains("active")) ?? rows.FirstOrDefault();
        return target?.Focus(method) == true;
    }

    // Rail -> content. Directional focus picks its target by geometry, so a rail row that lines up with
    // nothing in the pane leaves the remote in the menu (#201).
    private void OnRailKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Right || !ContentPane.IsEffectivelyVisible)
        {
            return;
        }

        e.Handled = Controls.PaneFocus.FocusFirst(ContentPane);
    }

    // Content -> rail. A control on the pane's left edge hands the focus back to the section menu.
    private void OnContentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not Key.Left || !RailPane.IsEffectivelyVisible)
        {
            return;
        }

        if (_topLevel?.FocusManager?.GetFocusedElement() is not Visual focused
            || Controls.PaneFocus.HasNeighbour(ContentPane, focused, NavigationDirection.Left))
        {
            return;
        }

        e.Handled = FocusRail(NavigationMethod.Directional);
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
