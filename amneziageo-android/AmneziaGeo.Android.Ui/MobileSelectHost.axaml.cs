using System;
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
using Button = Avalonia.Controls.Button;

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

}
