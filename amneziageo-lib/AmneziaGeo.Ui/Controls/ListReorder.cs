using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Перетаскивание элемента списка на новое место: мышь тянет сразу, палец - после удержания, а там, где
/// жест делит ось со свайпом, удержания ждут оба. Список перестраивается под указателем, сложенный порядок
/// уходит команде.
/// </summary>
internal sealed class ListReorder<TRow>
    where TRow : class
{
    // Travel that turns a press into a drag.
    private const double Threshold = 10;

    private const RoutingStrategies Both = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;

    // Hold that arms a drag, leaving shorter presses to the list's own scrolling and swiping.
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(450);

    // Перестановка с пульта переживает карточку: перемещение пересобирает её контейнер.
    private static TRow? _held;

    private static List<TRow>? _before;

    private static ICommand? _heldDrop;

    private readonly Control _item;
    private readonly bool _vertical;
    private readonly DispatcherTimer _hold;
    private ItemsControl? _list;
    private Panel? _panel;
    private ObservableCollection<TRow>? _rows;
    private TRow? _dragged;
    private ICommand? _drop;
    private Point _origin;
    private bool _pressed;
    private bool _armed;
    private bool _moved;

    /// <summary>
    /// ctor
    /// </summary>
    public ListReorder(Control item, bool vertical)
    {
        _item = item;
        _vertical = vertical;
        _hold = new DispatcherTimer { Interval = HoldTime };
        _hold.Tick += OnHeld;
    }

    /// <summary>
    /// Идёт ли перетаскивание.
    /// </summary>
    public bool Dragging { get; private set; }

    /// <summary>
    /// Ждать ли удержания и от мыши: на элементе со свайпом жесты делят ось.
    /// </summary>
    public bool HoldFirst { get; set; }

    /// <summary>
    /// Взят ли этот элемент в перестановку с пульта.
    /// </summary>
    public bool Ordering => _held is not null && ReferenceEquals(_held, _item.DataContext);

    /// <summary>
    /// Список, в котором стоит элемент.
    /// </summary>
    public ItemsControl? List => _item.FindAncestorOfType<ItemsControl>();

    /// <summary>
    /// Команда, принимающая сложенный порядок после броска. Берётся в начале жеста: перестановка
    /// пересобирает контейнер элемента и снимает с него привязки.
    /// </summary>
    public ICommand? Dropped { get; set; }

    /// <summary>
    /// Сколько элементов стоит в строке списка.
    /// </summary>
    public int Columns()
    {
        return List?.ItemsPanelRoot is UniformGrid { Columns: > 0 } grid ? grid.Columns : 1;
    }

    /// <summary>
    /// Берёт элемент в перестановку с пульта, запоминая порядок до неё.
    /// </summary>
    public bool Hold()
    {
        if (!Bind(out var rows, out var row))
        {
            return false;
        }

        _held = row;
        _before = [.. rows];
        _heldDrop = Dropped;
        return true;
    }

    /// <summary>
    /// Двигает взятый элемент на шаг, не записывая порядок. Возвращает новое место или -1.
    /// </summary>
    public int Nudge(int delta)
    {
        return Ordering ? Shift(delta) : -1;
    }

    /// <summary>
    /// Записывает порядок и отпускает элемент. Возвращает место элемента.
    /// </summary>
    public int Fix()
    {
        var drop = _heldDrop;
        var moved = Moved();
        var at = Place();
        Free();
        if (moved && drop?.CanExecute(null) == true)
        {
            drop.Execute(null);
        }

        return at;
    }

    /// <summary>
    /// Возвращает порядок, что был до взятия, и отпускает элемент. Возвращает место элемента.
    /// </summary>
    public int Undo()
    {
        var before = _before;
        if (before is not null && Bind(out var rows, out _))
        {
            for (var i = 0; i < before.Count; i++)
            {
                var at = rows.IndexOf(before[i]);
                if (at >= 0 && at != i)
                {
                    rows.Move(at, i);
                }
            }
        }

        var place = Place();
        Free();
        return place;
    }

    /// <summary>
    /// Принимает нажатие: мышь ждёт смещения, палец - удержания.
    /// </summary>
    public void Press(PointerPressedEventArgs e)
    {
        Cancel();
        _pressed = true;
        _origin = e.GetPosition(_item);
        _armed = !HoldFirst && e.Pointer.Type == PointerType.Mouse;
        if (!_armed)
        {
            _hold.Start();
        }
    }

    /// <summary>
    /// Ведёт элемент за указателем, пока список не взял жест на себя. Возвращает, взято ли движение.
    /// </summary>
    public bool Move(PointerEventArgs e)
    {
        if (Dragging)
        {
            return true;
        }

        if (!_pressed)
        {
            return false;
        }

        var point = e.GetPosition(_item);
        var travel = _vertical
            ? Math.Abs(point.Y - _origin.Y)
            : Math.Max(Math.Abs(point.X - _origin.X), Math.Abs(point.Y - _origin.Y));

        // A pointer that travels before the hold is scrolling or swiping: the drag never starts.
        if (!_armed)
        {
            if (travel >= Threshold)
            {
                Cancel();
            }

            return false;
        }

        return travel >= Threshold && Start(e);
    }

    /// <summary>
    /// Завершает жест на элементе. Возвращает, было ли перетаскивание.
    /// </summary>
    public bool Release()
    {
        if (Dragging)
        {
            Finish();
            return true;
        }

        Cancel();
        return false;
    }

    /// <summary>
    /// Бросает жест, оставляя список там, где он уже стоит.
    /// </summary>
    public void Cancel()
    {
        _hold.Stop();
        _pressed = false;
        _armed = false;
        if (Dragging)
        {
            Stop();
        }
    }

    // Место элемента в списке.
    private int Place()
    {
        return Bind(out var rows, out var row) ? rows.IndexOf(row) : -1;
    }

    // Порядок разошёлся с тем, что был снят при взятии.
    private bool Moved()
    {
        if (_before is not { } before || !Bind(out var rows, out _))
        {
            return false;
        }

        if (rows.Count != before.Count)
        {
            return true;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (!ReferenceEquals(rows[i], before[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static void Free()
    {
        _held = null;
        _before = null;
        _heldDrop = null;
    }

    // Переставляет элемент на шаг по списку, упираясь в его края.
    private int Shift(int delta)
    {
        if (delta == 0 || !Bind(out var rows, out var row))
        {
            return -1;
        }

        var from = rows.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= rows.Count)
        {
            return -1;
        }

        rows.Move(from, to);
        return to;
    }

    // Список и строка, за которую держится элемент.
    private bool Bind(out ObservableCollection<TRow> rows, out TRow row)
    {
        if (List is { ItemsSource: ObservableCollection<TRow> found } && _item.DataContext is TRow item)
        {
            rows = found;
            row = item;
            return true;
        }

        rows = default!;
        row = default!;
        return false;
    }

    // The hold arms what a finger's press alone leaves to the list.
    private void OnHeld(object? sender, EventArgs e)
    {
        _hold.Stop();
        _armed = _pressed;
    }

    // The list carries the drag, and the row it started on is remembered: a move rebuilds the element's
    // container, and a rebuilt container holds neither the gesture nor the row it stood for.
    private bool Start(PointerEventArgs e)
    {
        if (_item.FindAncestorOfType<ItemsControl>() is not { ItemsPanelRoot: { } panel } list
            || list.ItemsSource is not ObservableCollection<TRow> rows
            || _item.DataContext is not TRow dragged)
        {
            return false;
        }

        _list = list;
        _panel = panel;
        _rows = rows;
        _dragged = dragged;
        _drop = Dropped;
        _moved = false;
        Dragging = true;
        _item.Opacity = 0.6;
        e.Pointer.Capture(list);
        // Both phases: the captured list is the event's own target, and a target is not tunnelled through.
        list.AddHandler(InputElement.PointerMovedEvent, OnListMoved, Both);
        list.AddHandler(InputElement.PointerReleasedEvent, OnListReleased, Both);
        list.AddHandler(InputElement.PointerCaptureLostEvent, OnListCaptureLost, RoutingStrategies.Direct);
        Carry(e);
        return true;
    }

    private void OnListMoved(object? sender, PointerEventArgs e)
    {
        if (Dragging)
        {
            Carry(e);
            e.Handled = true;
        }
    }

    private void OnListReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Dragging)
        {
            e.Handled = true;
            Finish();
        }
    }

    // The pointer is freed before the release is delivered, so this, not the release, is where a drag usually
    // ends; the order it left the list in is stored either way.
    private void OnListCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        Finish();
    }

    // The drop stores the order the list now stands in; a drag that moved nothing stores nothing.
    private void Finish()
    {
        var moved = _moved;
        var drop = _drop;
        _hold.Stop();
        _pressed = false;
        _armed = false;
        Stop();
        if (moved && drop?.CanExecute(null) == true)
        {
            drop.Execute(null);
        }
    }

    private void Stop()
    {
        Dragging = false;
        _item.Opacity = 1;
        _list?.RemoveHandler(InputElement.PointerMovedEvent, OnListMoved);
        _list?.RemoveHandler(InputElement.PointerReleasedEvent, OnListReleased);
        _list?.RemoveHandler(InputElement.PointerCaptureLostEvent, OnListCaptureLost);
        _list = null;
        _panel = null;
        _rows = null;
        _dragged = null;
        _drop = null;
    }

    // Puts the dragged element in the slot the pointer stands over.
    private void Carry(PointerEventArgs e)
    {
        if (_panel is null || _rows is null || _dragged is null)
        {
            return;
        }

        var from = _rows.IndexOf(_dragged);
        if (from < 0)
        {
            return;
        }

        var point = e.GetPosition(_panel);
        for (var i = 0; i < _panel.Children.Count; i++)
        {
            if (_panel.Children[i].Bounds.Contains(point) && i != from)
            {
                _rows.Move(from, i);
                _moved = true;
                return;
            }
        }
    }
}
