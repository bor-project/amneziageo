using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AmneziaGeo.Ui.Controls;
using AmneziaGeo.Ui.Services;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;

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
    private TopLevel? _topLevel;
    private Control? _selectedRow;
    private int _transitionVersion;

    public MobileSelectHost(Control content)
    {
        InitializeComponent();
        _sheetTransform = (TranslateTransform)SelectSheet.RenderTransform!;
        _showSelect = Open;
        AdaptiveComboBox.SelectPresenter = _showSelect;
        RootGrid.Children.Insert(0, content);

        SizeChanged += OnHostSizeChanged;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        AdaptiveComboBox.SelectPresenter = _showSelect;
        AppSplitBridge.Register(ShowAppSplit);
        if (_topLevel is not null)
        {
            _topLevel.BackRequested += OnBackRequested;
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (AdaptiveComboBox.SelectPresenter == _showSelect)
        {
            AdaptiveComboBox.SelectPresenter = null;
        }

        if (_topLevel is not null)
        {
            _topLevel.BackRequested -= OnBackRequested;
            _topLevel = null;
        }

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
        if (_appSplitOverlay is not null)
        {
            e.Handled = true;
            CloseAppSplit();
            return;
        }

        if (!SelectOverlay.IsVisible)
        {
            return;
        }

        e.Handled = true;
        Close();
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

    // Full-screen per-app split picker for a profile: a mode selector over a checklist of installed apps. Writes
    // the choice straight to the in-process agent, which reconnects if the profile is live so it applies at once.
    private void ShowAppSplit(string profile)
    {
        if (_appSplitOverlay is not null)
        {
            return;
        }

        var agent = Services.AndroidAgentConnection.Current;
        if (agent is null)
        {
            return;
        }

        var (savedMode, savedPackages) = agent.GetAppSplit(profile);
        var selected = new HashSet<string>(savedPackages, StringComparer.Ordinal);
        var mode = string.IsNullOrEmpty(savedMode) ? "off" : savedMode;

        var pm = global::Android.App.Application.Context.PackageManager!;
        var own = global::Android.App.Application.Context.PackageName;
        var apps = pm.GetInstalledApplications(global::Android.Content.PM.PackageInfoFlags.MetaData)
            .Where(a => a.PackageName is not null && a.PackageName != own
                && ((a.Flags & global::Android.Content.PM.ApplicationInfoFlags.System) == 0
                    || (a.Flags & global::Android.Content.PM.ApplicationInfoFlags.UpdatedSystemApp) != 0))
            .Select(a => (Label: a.LoadLabel(pm)?.ToString() ?? a.PackageName!, Pkg: a.PackageName!))
            .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var listPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        var checks = new List<CheckBox>();
        var rows = new List<(CheckBox Check, string Haystack)>();
        foreach (var app in apps)
        {
            var check = new CheckBox { Content = app.Label, Tag = app.Pkg, IsChecked = selected.Contains(app.Pkg) };
            checks.Add(check);
            rows.Add((check, $"{app.Label} {app.Pkg}".ToLowerInvariant()));
            listPanel.Children.Add(check);
        }

        var listScroll = new ScrollViewer { Content = listPanel, IsEnabled = mode != "off" };

        // Live match filter over the app list.
        var search = new TextBox { Watermark = "Поиск приложений", Margin = new Thickness(0, 8, 0, 0), IsEnabled = mode != "off" };
        search.Classes.Add("field");
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            foreach (var (check, haystack) in rows)
            {
                check.IsVisible = query.Length == 0 || haystack.Contains(query, StringComparison.Ordinal);
            }
        };

        var modes = new[] { ("off", "Все приложения"), ("include", "Только выбранные"), ("exclude", "Все, кроме выбранных") };
        var modePanel = new StackPanel { Spacing = 6 };
        var modeButtons = new List<Button>();
        foreach (var (value, label) in modes)
        {
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = value };
            button.Classes.Add("methodpick");
            if (value == mode)
            {
                button.Classes.Add("active");
            }

            button.Click += (_, _) =>
            {
                mode = value;
                listScroll.IsEnabled = mode != "off";
                search.IsEnabled = mode != "off";
                foreach (var other in modeButtons)
                {
                    other.Classes.Remove("active");
                }

                button.Classes.Add("active");
            };
            modeButtons.Add(button);
            modePanel.Children.Add(button);
        }

        var save = new Button { Content = "Сохранить", HorizontalAlignment = HorizontalAlignment.Stretch };
        save.Classes.Add("accent");
        save.Click += (_, _) =>
        {
            var packages = mode == "off"
                ? new List<string>()
                : checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
            agent.SetAppSplit(profile, mode, packages);
            CloseAppSplit();
        };

        var cancel = new Button { Content = "Отмена", HorizontalAlignment = HorizontalAlignment.Stretch };
        cancel.Classes.Add("softbtn");
        cancel.Click += (_, _) => CloseAppSplit();

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(cancel, 0);
        Grid.SetColumn(save, 1);
        actions.Children.Add(cancel);
        actions.Children.Add(save);

        var title = new TextBlock { Text = profile, FontWeight = FontWeight.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 8) };
        var hint = new TextBlock
        {
            Text = "Изменение применится при следующем подключении.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        hint.Classes.Add("muted");

        var header = new StackPanel { Children = { title, modePanel, hint } };

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
