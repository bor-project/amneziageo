using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Перетаскивает карточку после удержания.
/// </summary>
internal sealed class TouchListReorder<TRow>
    where TRow : class
{
    private const double Threshold = 10;
    private const RoutingStrategies Both = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
    private readonly Control _item;
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
    public TouchListReorder(Control item)
    {
        _item = item;
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _hold.Tick += OnHeld;
    }

    public bool Dragging { get; private set; }

    public ICommand? Dropped { get; set; }

    public void Press(PointerPressedEventArgs e)
    {
        Cancel();
        _pressed = true;
        _origin = e.GetPosition(_item);
        _hold.Start();
    }

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
        var travel = Math.Max(Math.Abs(point.X - _origin.X), Math.Abs(point.Y - _origin.Y));
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

    private void OnHeld(object? sender, EventArgs e)
    {
        _hold.Stop();
        _armed = _pressed;
    }

    private bool Start(PointerEventArgs e)
    {
        if (_item.FindAncestorOfType<ItemsControl>() is not { ItemsPanelRoot: { } panel } list
            || list.ItemsSource is not ObservableCollection<TRow> rows
            || _item.DataContext is not TRow row)
        {
            return false;
        }

        _list = list;
        _panel = panel;
        _rows = rows;
        _dragged = row;
        _drop = Dropped;
        _moved = false;
        Dragging = true;
        _item.Opacity = 0.6;
        e.Pointer.Capture(list);
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

    private void OnListCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        Finish();
    }

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
