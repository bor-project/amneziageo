using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmneziaGeo.Localization;
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

    // Кто открыл шторку: ему возвращается фокус, когда она уходит.
    private Control? _sheetOrigin;

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

    // Пульту и клавиатуре нужна точка входа в шторку: иначе стрелки ходят по экрану за ней. Закрываясь, шторка
    // уносит с собой строку, на которой стоял фокус, - вернуть его тому, кто её открыл.
    private void OnSheetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ActionSheetViewModel.IsOpen))
        {
            return;
        }

        if (_vm?.Sheet.IsOpen == true)
        {
            _sheetOrigin = _topLevel?.FocusManager?.GetFocusedElement() as Control;
            Dispatcher.UIThread.Post(
                () => Controls.PaneFocus.FocusFirst(SheetOptions),
                DispatcherPriority.Loaded);
            return;
        }

        var origin = _sheetOrigin;
        _sheetOrigin = null;
        Dispatcher.UIThread.Post(
            () =>
            {
                // Способ мог увести на другой экран - там свой фокус.
                if (origin is null || !origin.IsEffectivelyVisible)
                {
                    return;
                }

                if (_topLevel?.FocusManager?.GetFocusedElement() is Visual live && live.GetVisualRoot() is not null)
                {
                    return;
                }

                origin.Focus(NavigationMethod.Directional);
            },
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

        if (_vm is null)
        {
            return false;
        }

        // Home itself hands the key to the system.
        if (!_vm.IsSettings)
        {
            return false;
        }

        // Out of the content pane the step back lands in the section menu, not on home; a sub-view of the
        // section closes first, as it always did.
        if (!_vm.SettingsStepsBack
            && RailPane.IsEffectivelyVisible
            && _topLevel?.FocusManager?.GetFocusedElement() is Visual inPane
            && ContentPane.IsVisualAncestorOf(inPane)
            && FocusRail(NavigationMethod.Directional))
        {
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
                HomePowerButton.Focus(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Unspecified);
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

    // A footer action takes its own bar off the screen; without this the focus falls out of the pane onto the
    // header's back arrow instead of the section the edit belonged to.
    private void OnFooterAction(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_topLevel?.FocusManager?.GetFocusedElement() is Visual focused
                    && ContentPane.IsVisualAncestorOf(focused))
                {
                    return;
                }

                Controls.PaneFocus.FocusFirst(ContentPane);
            },
            DispatcherPriority.Loaded);
    }

    // The update row on home opens the sheet that carries the step itself: download, install or cancel the
    // download, next to dropping the offer and to leaving it as it stands.
    private void OnHomeUpdate(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var general = _vm.General;
        var options = new List<ActionOption>();
        if (general.DownloadActive)
        {
            options.Add(new ActionOption(
                Loc.Instance.Get("Main_CancelDownloadButton"),
                Glyphs.Close,
                () => general.CancelDownloadCommand.Execute(null)));
        }
        else if (general.UpdateDownloaded)
        {
            options.Add(new ActionOption(
                Loc.Instance.Get("Main_InstallButton"),
                Glyphs.Install,
                () => general.ApplyUpdateCommand.Execute(null)));
        }
        else
        {
            options.Add(new ActionOption(
                Loc.Instance.Get("Main_DownloadButton"),
                Glyphs.Download,
                () => general.DownloadUpdateCommand.Execute(null)));
        }

        if (!general.DownloadActive)
        {
            options.Add(new ActionOption(
                Loc.Instance.Get("Main_HideButton"),
                Glyphs.Close,
                () => general.DismissUpdateBannerCommand.Execute(null)));
        }

        options.Add(new ActionOption(Loc.Instance.Get("Main_CancelButton"), Glyphs.Close, () => { }));

        Controls.ActionOptions.Present(
            sender as Control,
            _vm.Sheet,
            Loc.Instance.Get("Main_UpdateSection"),
            general.UpdateBannerText,
            options);
    }

    // «Добавить конфигурацию» на главном: способ выбирается здесь же, настройки открываются уже под него.
    private void OnAddConfigOptions(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        Controls.ConfigAddOptions.Present(sender as Control, this, _vm.Config, _ => _vm.OpenConfigImport());
    }

    // Up / down inside a pane walk the tab order instead of the geometry: a narrow control standing beside a
    // wide one (a link over a field, a button in a row) never lies under the moving edge, so directional focus
    // steps over it and the remote can never reach it.
    private void OnPaneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not (Key.Down or Key.Up) || sender is not Visual pane)
        {
            return;
        }

        e.Handled = MoveInTabOrder(pane, e.Key is Key.Down);
    }

    // The sheet stands over a screen whose own controls lie right behind its rows, so its keys wrap inside the
    // card instead of handing the focus to what the sheet covers.
    private void OnSheetKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not (Key.Down or Key.Up) || sender is not Visual card)
        {
            return;
        }

        e.Handled = MoveInTabOrder(card, e.Key is Key.Down, wrap: true);
    }

    private bool MoveInTabOrder(Visual pane, bool forward, bool wrap = false)
    {
        // A multiline box spends the key on its own caret.
        if (_topLevel?.FocusManager?.GetFocusedElement() is not Visual focused
            || focused is TextBox { AcceptsReturn: true }
            || !pane.IsVisualAncestorOf(focused))
        {
            return false;
        }

        var direction = forward ? NavigationDirection.Next : NavigationDirection.Previous;
        var step = (IInputElement)focused;
        for (var i = 0; i < 200; i++)
        {
            if (KeyboardNavigationHandler.GetNext(step, direction) is not Control next)
            {
                return false;
            }

            if (pane.IsVisualAncestorOf(next))
            {
                next.BringIntoView();
                return next.Focus(NavigationMethod.Directional);
            }

            // Off the pane: a trapped pane walks the rest of the cycle back to its own first stop.
            if (!wrap)
            {
                return false;
            }

            step = next;
        }

        return false;
    }

    // Content -> rail. A control on the pane's left edge hands the focus back to the section menu.
    private void OnContentKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && e.Key is Key.Down or Key.Up)
        {
            e.Handled = MoveInTabOrder(ContentPane, e.Key is Key.Down);
            return;
        }

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
