using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Плавная перестановка каталога: элемент, сменивший место, доезжает до него с прежнего. Места снимаются
/// после каждой компоновки, поэтому жест значения не имеет - драг, стрелки и пульт двигают одинаково.
/// </summary>
internal sealed class ListMotion
{
    /// <summary>
    /// Включает плавную перестановку на списке.
    /// </summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("Enabled", typeof(ListMotion));

    // Время проезда до нового места.
    private static readonly TimeSpan Travel = TimeSpan.FromMilliseconds(180);

    // Перестановка держится за список, а не за карточку: карточку она пересобирает.
    private static readonly AttachedProperty<ListMotion?> StateProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, ListMotion?>("State", typeof(ListMotion));

    private readonly ItemsControl _list;
    private readonly Dictionary<object, Rect> _seats = [];
    private INotifyCollectionChanged? _rows;
    private bool _moved;

    static ListMotion()
    {
        EnabledProperty.Changed.AddClassHandler<ItemsControl>(OnEnabledChanged);
    }

    /// <summary>
    /// ctor
    /// </summary>
    private ListMotion(ItemsControl list)
    {
        _list = list;
        _list.AttachedToVisualTree += OnAttached;
        _list.PropertyChanged += OnListChanged;
        Follow();
        Watch();
    }

    public static void SetEnabled(ItemsControl target, bool value) => target.SetValue(EnabledProperty, value);

    public static bool GetEnabled(ItemsControl target) => target.GetValue(EnabledProperty);

    private static void OnEnabledChanged(ItemsControl list, AvaloniaPropertyChangedEventArgs e)
    {
        list.GetValue(StateProperty)?.Release();
        list.SetValue(StateProperty, e.NewValue is true ? new ListMotion(list) : null);
    }

    // Ставит элемент на прежнее место и отпускает его на новое.
    private static void Slide(Control child, Rect was, Rect seat)
    {
        var dx = was.X - seat.X;
        var dy = was.Y - seat.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
        {
            return;
        }

        var from = new TransformOperations.Builder(1);
        from.AppendTranslate(dx, dy);
        child.Transitions = null;
        child.RenderTransform = from.Build();
        child.Transitions =
        [
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = Travel,
                Easing = new CubicEaseOut(),
            },
        ];
        child.RenderTransform = TransformOperations.Identity;
    }

    // Следит за списком, которым набит контрол.
    private void Follow()
    {
        if (_rows is not null)
        {
            _rows.CollectionChanged -= OnRowsChanged;
        }

        _rows = _list.ItemsSource as INotifyCollectionChanged;
        if (_rows is not null)
        {
            _rows.CollectionChanged += OnRowsChanged;
        }

        _seats.Clear();
    }

    // Пересаживает подписку на компоновку: пока список вне дерева, слушать её некому.
    private void Watch()
    {
        _list.LayoutUpdated -= OnLaid;
        _list.LayoutUpdated += OnLaid;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Watch();
    }

    private void OnListChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ItemsControl.ItemsSourceProperty)
        {
            Follow();
        }
    }

    // Перестановка ждёт компоновки; всякая другая правка списка снятые места обнуляет.
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            _moved = true;
        }
        else
        {
            _seats.Clear();
        }
    }

    // Сверяет места с теми, что были до перестановки, и пускает разъехавшиеся элементы своим ходом.
    private void OnLaid(object? sender, EventArgs e)
    {
        if (!_list.IsEffectivelyVisible || _list.ItemsPanelRoot is not { } panel)
        {
            _moved = false;
            _seats.Clear();
            return;
        }

        var moved = _moved;
        _moved = false;
        foreach (var child in panel.Children)
        {
            if (child.DataContext is not { } row)
            {
                continue;
            }

            if (moved && _seats.TryGetValue(row, out var was))
            {
                Slide(child, was, child.Bounds);
            }

            _seats[row] = child.Bounds;
        }
    }

    // Отпускает список.
    private void Release()
    {
        _list.AttachedToVisualTree -= OnAttached;
        _list.PropertyChanged -= OnListChanged;
        _list.LayoutUpdated -= OnLaid;
        if (_rows is not null)
        {
            _rows.CollectionChanged -= OnRowsChanged;
            _rows = null;
        }
    }
}
