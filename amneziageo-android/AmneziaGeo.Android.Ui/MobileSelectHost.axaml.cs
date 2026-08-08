using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmneziaGeo.Ui.Controls;
using AmneziaGeo.Ui.Services;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;
using InputMethod = Avalonia.Input.InputMethod;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Android-only host that presents every ComboBox as a bottom sheet.
/// </summary>
internal sealed partial class MobileSelectHost : UserControl
{
    private static readonly TimeSpan _transitionDuration = TimeSpan.FromMilliseconds(160);

    private ComboBox? _activeComboBox;
    private readonly TranslateTransform _sheetTransform;
    private readonly Action<AdaptiveComboBox> _showSelect;
    private readonly Func<bool> _dismissKeyboard;
    private TopLevel? _topLevel;
    private Control? _selectedRow;
    private TextBox? _keyboardTarget;
    private Control? _lastFocus;
    private Control? _disabledFocus;
    private int _transitionVersion;

    public MobileSelectHost(Control content)
    {
        InitializeComponent();
        _sheetTransform = (TranslateTransform)SelectSheet.RenderTransform!;
        _showSelect = Open;
        _dismissKeyboard = DismissKeyboard;
        AdaptiveComboBox.SelectPresenter = _showSelect;
        RootGrid.Children.Insert(0, content);

        // A remote drives focus across the whole screen, so a text field must not summon the keyboard just by
        // being focused - it would swallow every key, Escape included. The select press raises it instead.
        if (UiPlatform.IsTelevision)
        {
            Styles.Add(new Style(x => x.OfType<TextBox>())
            {
                Setters = { new Setter(InputMethod.IsInputMethodEnabledProperty, false) },
            });
        }

        SizeChanged += OnHostSizeChanged;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        AdaptiveComboBox.SelectPresenter = _showSelect;
        AppSplitBridge.Register(ShowAppPicker);
        SoftInputBridge.Register(_dismissKeyboard);
        // TV file managers answer the single-file picker with a multi-select payload it never reads, so the pick
        // comes back empty; the built-in browser owns opening there.
        if (UiPlatform.IsTelevision || !HasHandler(global::Android.Content.Intent.ActionOpenDocument))
        {
            FileBrowserHost.Register((title, extensions) => FileBrowserOverlay.ShowAsync(RootGrid, title, extensions));
        }

        // Opening and saving resolve separately: a phone may carry a third-party picker for open and still have
        // nothing but the stub for create, which accepts the intent and writes nothing.
        if (UiPlatform.IsTelevision || !HasHandler(global::Android.Content.Intent.ActionCreateDocument))
        {
            FileSaverHost.Register((title, name) => FileBrowserOverlay.SaveAsync(RootGrid, title, name));
        }

        if (_topLevel is not null)
        {
            _topLevel.BackRequested += OnBackRequested;
            _topLevel.AddHandler(KeyDownEvent, OnTopLevelKeyPreview, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Bubble);
            _topLevel.AddHandler(GotFocusEvent, OnTopLevelGotFocus, RoutingStrategies.Bubble);
            _topLevel.AddHandler(LostFocusEvent, OnTopLevelLostFocus, RoutingStrategies.Bubble);
            _topLevel.AddHandler(PointerReleasedEvent, OnTopLevelPointerReleased, RoutingStrategies.Tunnel);
        }

        MainActivity.Resumed += OnActivityResumed;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (AdaptiveComboBox.SelectPresenter == _showSelect)
        {
            AdaptiveComboBox.SelectPresenter = null;
        }

        SoftInputBridge.Unregister(_dismissKeyboard);

        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            _topLevel.RemoveHandler(KeyDownEvent, OnTopLevelKeyPreview);
            _topLevel.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            _topLevel.RemoveHandler(GotFocusEvent, OnTopLevelGotFocus);
            _topLevel.RemoveHandler(LostFocusEvent, OnTopLevelLostFocus);
            _topLevel.RemoveHandler(PointerReleasedEvent, OnTopLevelPointerReleased);
            _topLevel = null;
        }

        MainActivity.Resumed -= OnActivityResumed;

        CloseImmediately();
        base.OnDetachedFromVisualTree(e);
    }

    private void Open(AdaptiveComboBox comboBox)
    {
        var items = (comboBox.ItemsSource ?? comboBox.Items).Cast<object?>().ToArray();
        if (items.Length == 0)
        {
            return;
        }

        _transitionVersion++;
        _activeComboBox = comboBox;
        _selectedRow = null;
        OptionsPanel.Children.Clear();

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var content = item;
            var template = comboBox.ItemTemplate;
            var enabled = true;

            if (item is ComboBoxItem comboBoxItem)
            {
                content = comboBoxItem.Content;
                template = comboBoxItem.ContentTemplate;
                enabled = comboBoxItem.IsEffectivelyEnabled;
            }

            var contentControl = new ContentControl
            {
                Content = content,
                ContentTemplate = template,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            contentControl.Classes.Add("mobile-select-content");

            var marker = new Grid
            {
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    SelectionRing(),
                    new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = Avalonia.Media.Brushes.White,
                        IsVisible = index == comboBox.SelectedIndex,
                    },
                },
            };

            var rowContent = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 12,
                Children = { contentControl, marker },
            };
            Grid.SetColumn(marker, 1);

            var row = new Button
            {
                Content = rowContent,
                IsEnabled = enabled,
            };
            row.Classes.Add("mobile-select-option");
            if (index == comboBox.SelectedIndex)
            {
                row.Classes.Add("selected");
                _selectedRow = row;
            }

            var selectedIndex = index;
            row.Click += (_, _) => Select(selectedIndex);
            OptionsPanel.Children.Add(row);
        }

        UpdateSheetHeight(Bounds.Height);
        SelectOverlay.IsVisible = true;
        SelectOverlay.Opacity = 0;
        _sheetTransform.Y = 32;

        Dispatcher.UIThread.Post(() =>
        {
            if (!SelectOverlay.IsVisible)
            {
                return;
            }

            SelectOverlay.Opacity = 1;
            _sheetTransform.Y = 0;
            _selectedRow?.BringIntoView();
            _selectedRow?.Focus();
        }, DispatcherPriority.Render);
    }

    private static Ellipse SelectionRing()
    {
        var ring = new Ellipse
        {
            Width = 18,
            Height = 18,
            StrokeThickness = 2,
        };
        ring.Classes.Add("mobile-select-ring");
        return ring;
    }

    private void Select(int index)
    {
        if (_activeComboBox is { } comboBox && index >= 0 && index < comboBox.ItemCount)
        {
            comboBox.SelectedIndex = index;
        }

        Close();
    }

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (!e.Handled && CloseTopOverlay())
        {
            e.Handled = true;
        }
    }

    // Arrows on a field that is not being edited must move focus, not the caret: a multiline config would
    // otherwise cost a dozen presses to cross. Parking the caret on the edge makes the field release the key
    // to directional navigation on the first press.
    private void OnTopLevelKeyPreview(object? sender, KeyEventArgs e)
    {
        if (e.Handled
            || e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right)
            || _topLevel?.FocusManager?.GetFocusedElement() is not TextBox box
            || InputMethod.GetIsInputMethodEnabled(box))
        {
            return;
        }

        box.CaretIndex = e.Key is Key.Up or Key.Left ? 0 : box.Text?.Length ?? 0;
    }

    // Escape is taken at the top level, ahead of the shell: an overlay closes before the shell steps back.
    // The select press on a focused text field is taken here too, since that is what opens the keyboard on TV.
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key is Key.Escape && CloseTopOverlay())
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Space or Key.Enter
            && _topLevel?.FocusManager?.GetFocusedElement() is TextBox box
            && !InputMethod.GetIsInputMethodEnabled(box))
        {
            OpenKeyboard(box);
            e.Handled = true;
        }
    }

    // Hands the field to the input method and re-seats focus, which is what makes the manager open the keyboard.
    private void OpenKeyboard(TextBox box)
    {
        _keyboardTarget = box;
        InputMethod.SetIsInputMethodEnabled(box, true);
        _topLevel?.FocusManager?.ClearFocus();
        box.Focus(NavigationMethod.Directional);
        _keyboardTarget = null;
    }

    // Takes the field back from the input method and re-seats focus, which drops the keyboard and keeps the field.
    private void CloseKeyboard(TextBox box)
    {
        _keyboardTarget = box;
        InputMethod.SetIsInputMethodEnabled(box, false);
        _topLevel?.FocusManager?.ClearFocus();
        box.Focus(NavigationMethod.Directional);
        _keyboardTarget = null;
    }

    // Raises the keyboard again on a tap into the field that already holds focus: Avalonia offers it on a focus
    // change only, so the field the back button silenced would otherwise stay mute.
    private void OnTopLevelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_topLevel?.FocusManager?.GetFocusedElement() is TextBox box
            && !InputMethod.GetIsInputMethodEnabled(box)
            && e.Source is Visual source
            && ReferenceEquals(source.FindAncestorOfType<TextBox>(true), box))
        {
            OpenKeyboard(box);
        }
    }

    private void OnTopLevelGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is Control control and not TopLevel)
        {
            _lastFocus = control;
        }
    }

    // A system picker drops focus on the way back, leaving the remote to start over from the header. Puts it
    // where the user left it, or on the first control the screen offers.
    private void OnActivityResumed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_topLevel?.FocusManager?.GetFocusedElement() is Control and not TopLevel)
            {
                return;
            }

            if (_lastFocus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } && _lastFocus.GetVisualRoot() is not null)
            {
                _lastFocus.Focus(NavigationMethod.Directional);
                return;
            }

            if (KeyboardNavigationHandler.GetNext(RootGrid, NavigationDirection.Next) is { } first)
            {
                first.Focus(NavigationMethod.Directional);
            }
        });
    }

    // Leaving a field puts it back under the style: the next visit needs another select press.
    private void OnTopLevelLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox box && !ReferenceEquals(box, _keyboardTarget))
        {
            box.ClearValue(InputMethod.IsInputMethodEnabledProperty);
        }

        if (e.Source is not Control control)
        {
            return;
        }

        if (!control.IsEffectivelyVisible || control.GetVisualRoot() is null)
        {
            RecoverFocus(control);
        }
        else if (!control.IsEffectivelyEnabled)
        {
            HoldFocus(control);
        }
    }

    // A command that switches its own button off for the run - a busy flag on the entry just pressed - takes the
    // focus ring with it, and the next remote press starts over from the header. Waits for the button to come
    // round and puts the ring back on it.
    private void HoldFocus(Control lost)
    {
        if (_disabledFocus is not null)
        {
            _disabledFocus.PropertyChanged -= OnDisabledFocusChanged;
        }

        _disabledFocus = lost;
        lost.PropertyChanged += OnDisabledFocusChanged;
    }

    private void OnDisabledFocusChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != InputElement.IsEffectivelyEnabledProperty
            || sender is not Control control || !control.IsEffectivelyEnabled)
        {
            return;
        }

        control.PropertyChanged -= OnDisabledFocusChanged;
        _disabledFocus = null;

        if (_topLevel?.FocusManager?.GetFocusedElement() is Control and not TopLevel)
        {
            return;
        }

        if (control.IsEffectivelyVisible && control.GetVisualRoot() is not null)
        {
            control.Focus(NavigationMethod.Directional);
        }
    }

    // A control that hides itself - a delete trigger swapped for its confirm pair, an import picker replaced by
    // the editor - takes the focus ring with it and strands the remote on the header. Puts the ring on whatever
    // took its place.
    private void RecoverFocus(Control lost)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_topLevel?.FocusManager?.GetFocusedElement() is Control and not TopLevel)
            {
                return;
            }

            for (var scope = lost.GetVisualParent() as Control; scope is not null; scope = scope.GetVisualParent() as Control)
            {
                if (!scope.IsEffectivelyVisible || scope.GetVisualRoot() is null)
                {
                    continue;
                }

                if (KeyboardNavigationHandler.GetNext(scope, NavigationDirection.Next) is { } next)
                {
                    next.Focus(NavigationMethod.Directional);
                    return;
                }
            }
        });
    }

    // Dismisses the topmost overlay in stacking order; the keyboard covers everything and goes first.
    private bool CloseTopOverlay()
    {
        if (DismissKeyboard())
        {
            return true;
        }

        if (FileBrowserOverlay.Current is { } browser)
        {
            browser.Back();
            return true;
        }

        if (_appSplitOverlay is not null)
        {
            CloseAppSplit();
            return true;
        }

        if (SelectOverlay.IsVisible)
        {
            Close();
            return true;
        }

        return false;
    }

    // Takes the keyboard off the field being edited, reporting whether there was one. The pane answers what the
    // platform really shows: the field's own flag comes back to its default once the field is focused again, and
    // a second press would spend itself on a keyboard that is already down.
    private bool DismissKeyboard()
    {
        if (_topLevel is not { } top)
        {
            return false;
        }

        var typing = top.FocusManager?.GetFocusedElement() as TextBox;
        var open = top.InputPane is { } pane
            ? pane.State == InputPaneState.Open
            : typing is not null && InputMethod.GetIsInputMethodEnabled(typing);

        if (!open)
        {
            return false;
        }

        if (typing is not null)
        {
            CloseKeyboard(typing);
        }
        else
        {
            top.FocusManager?.ClearFocus();
        }

        return true;
    }

    // Whether a real activity handles the intent: Android TV images ship only a stub that toasts and returns.
    private static bool HasHandler(string action)
    {
        var manager = global::Android.App.Application.Context.PackageManager;
        if (manager is null)
        {
            return false;
        }

        var intent = new global::Android.Content.Intent(action);
        intent.AddCategory(global::Android.Content.Intent.CategoryOpenable);
        intent.SetType("*/*");
        return manager.QueryIntentActivities(intent, global::Android.Content.PM.PackageInfoFlags.MetaData)
            .Any(info => info.ActivityInfo?.PackageName is { } package
                && !package.Contains("frameworkpackagestubs", StringComparison.Ordinal));
    }

    private void Close()
    {
        if (!SelectOverlay.IsVisible)
        {
            return;
        }

        var comboBox = _activeComboBox;
        _activeComboBox = null;
        _selectedRow = null;
        var version = ++_transitionVersion;

        SelectOverlay.Opacity = 0;
        _sheetTransform.Y = 32;
        DispatcherTimer.RunOnce(() =>
        {
            if (version != _transitionVersion)
            {
                return;
            }

            SelectOverlay.IsVisible = false;
            OptionsPanel.Children.Clear();
            comboBox?.Focus();
        }, _transitionDuration);
    }

    private void CloseImmediately()
    {
        _transitionVersion++;
        _activeComboBox = null;
        _selectedRow = null;
        SelectOverlay.IsVisible = false;
        SelectOverlay.Opacity = 0;
        _sheetTransform.Y = 32;
        OptionsPanel.Children.Clear();
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateSheetHeight(e.NewSize.Height);

    private void UpdateSheetHeight(double height)
    {
        if (height > 0)
        {
            OptionsScroll.MaxHeight = Math.Max(160, height * 0.68);
        }
    }

    private Border? _appSplitOverlay;

    // Full-screen app picker: a searchable checklist of launchable apps, pre-checked from the current selection.
    // Enumerates via the launcher intent (no QUERY_ALL_PACKAGES), so only apps with a launcher icon are listed.
    private void ShowAppPicker(IReadOnlyCollection<string> selected, Action<IReadOnlyCollection<string>> onPicked)
    {
        if (_appSplitOverlay is not null)
        {
            return;
        }

        var chosen = new HashSet<string>(selected, StringComparer.Ordinal);

        var pm = global::Android.App.Application.Context.PackageManager!;
        var own = global::Android.App.Application.Context.PackageName;
        var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionMain);
        intent.AddCategory(global::Android.Content.Intent.CategoryLauncher);
        var apps = pm.QueryIntentActivities(intent, global::Android.Content.PM.PackageInfoFlags.MetaData)
            .Where(ri => ri.ActivityInfo?.ApplicationInfo?.PackageName is not null
                && ri.ActivityInfo.ApplicationInfo.PackageName != own)
            .Select(ri =>
            {
                var ai = ri.ActivityInfo!.ApplicationInfo!;
                return (Label: ri.LoadLabel(pm)?.ToString() ?? ai.PackageName!, Pkg: ai.PackageName!);
            })
            .GroupBy(a => a.Pkg, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var listPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        var rows = new List<(CheckBox Check, string Haystack)>();
        foreach (var app in apps)
        {
            var check = new CheckBox { Content = app.Label, Tag = app.Pkg, IsChecked = chosen.Contains(app.Pkg) };
            rows.Add((check, $"{app.Label} {app.Pkg}".ToLowerInvariant()));
            listPanel.Children.Add(check);
        }

        var listScroll = new ScrollViewer { Content = listPanel };

        // Live match filter over the app list.
        var search = new TextBox { Watermark = "Поиск приложений", Margin = new Thickness(0, 8, 0, 0) };
        search.Classes.Add("field");
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            foreach (var (check, haystack) in rows)
            {
                check.IsVisible = query.Length == 0 || haystack.Contains(query, StringComparison.Ordinal);
            }
        };

        var save = new Button { Content = "Сохранить", HorizontalAlignment = HorizontalAlignment.Stretch };
        save.Classes.Add("accent");
        save.Click += (_, _) =>
        {
            var packages = rows.Where(r => r.Check.IsChecked == true).Select(r => (string)r.Check.Tag!).ToList();
            CloseAppSplit();
            onPicked(packages);
        };

        var cancel = new Button { Content = "Отмена", HorizontalAlignment = HorizontalAlignment.Stretch };
        cancel.Classes.Add("softbtn");
        cancel.Click += (_, _) => CloseAppSplit();

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(cancel, 0);
        Grid.SetColumn(save, 1);
        actions.Children.Add(cancel);
        actions.Children.Add(save);

        var title = new TextBlock { Text = "Приложения", FontWeight = FontWeight.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 8) };
        var hint = new TextBlock
        {
            Text = "Изменение применится при следующем подключении.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        hint.Classes.Add("muted");

        var header = new StackPanel { Children = { title, hint } };

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(search);
        panel.Children.Add(actions);
        panel.Children.Add(listScroll);

        var overlay = new Border { Child = panel };
        _appSplitOverlay = overlay;
        RootGrid.Children.Add(overlay);
        overlay.Background = overlay.TryFindResource("AgPanelBrush", out var brush) && brush is IBrush found
            ? found
            : new SolidColorBrush(Color.FromRgb(0x1a, 0x1c, 0x20));
    }

    private void CloseAppSplit()
    {
        if (_appSplitOverlay is not null)
        {
            RootGrid.Children.Remove(_appSplitOverlay);
            _appSplitOverlay = null;
        }
    }

}
