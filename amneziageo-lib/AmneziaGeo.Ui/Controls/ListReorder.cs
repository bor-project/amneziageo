using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Управляет режимом перемещения карточки.
/// </summary>
internal sealed class ListReorder<TRow>
    where TRow : class
{
    private static readonly ReorderSession<TRow> Session = new();
    private static readonly HashSet<ListReorder<TRow>> Attached = [];
    private static readonly HashSet<Visual> Watched = [];
    private readonly Control _item;
    private readonly DispatcherTimer _hold;
    private Point _origin;
    private bool _pressed;
    private bool _consumed;
    private static bool _checking;

    static ListReorder()
    {
        Session.Changed += OnSessionChanged;
    }

    /// <summary>
    /// ctor
    /// </summary>
    public ListReorder(Control item)
    {
        _item = item;
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hold.Tick += OnHeld;
    }

    public event Action? Requested;

    public event Action? Changed;

    public bool Ordering => ReferenceEquals(Session.Row, _item.DataContext)
        && Session.Row is not null && ReferenceEquals(Session.Rows, List?.ItemsSource);

    public ItemsControl? List => _item.FindAncestorOfType<ItemsControl>();

    public ICommand? Dropped { get; set; }

    public int Columns() => List?.ItemsPanelRoot is UniformGrid { Columns: > 0 } grid ? grid.Columns : 1;

    public bool CanNudge(int delta) => Ordering && Session.CanMove(delta, Columns());

    public void Attach()
    {
        Attached.Add(this);
        Changed?.Invoke();
    }

    public void Detach()
    {
        Cancel();
        Attached.Remove(this);
        CheckVisibility();
    }

    public bool Hold()
    {
        if (List is not { ItemsSource: ObservableCollection<TRow> rows } || _item.DataContext is not TRow row)
        {
            return false;
        }

        var drop = Dropped;
        Session.Begin(rows, row, () =>
        {
            if (drop?.CanExecute(null) == true)
            {
                drop.Execute(null);
            }
        });
        return Ordering;
    }

    public int Nudge(int delta) => Ordering ? Session.Move(delta, Columns()) : -1;

    public int Fix() => Ordering ? Session.Commit() : -1;

    public int Undo() => Ordering ? Session.Cancel() : -1;

    public void Press(PointerPressedEventArgs e)
    {
        Cancel();
        _consumed = false;
        if (Session.Row is not null
            || !e.GetCurrentPoint(_item).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || source.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault() != _item.FindControl<Button>("FacePart"))
        {
            return;
        }

        _pressed = true;
        _origin = e.GetPosition(_item);
        _hold.Start();
    }

    public bool Move(PointerEventArgs e)
    {
        if (_pressed)
        {
            var point = e.GetPosition(_item);
            if (Math.Max(Math.Abs(point.X - _origin.X), Math.Abs(point.Y - _origin.Y)) >= 10)
            {
                Cancel();
            }
        }

        return false;
    }

    public bool Release()
    {
        Cancel();
        var consumed = _consumed;
        _consumed = false;
        return consumed;
    }

    public void Cancel()
    {
        _hold.Stop();
        _pressed = false;
    }

    private void OnHeld(object? sender, EventArgs e)
    {
        _hold.Stop();
        if (_pressed)
        {
            _pressed = false;
            _consumed = true;
            Requested?.Invoke();
        }
    }

    private static void OnSessionChanged()
    {
        Watch();
        foreach (var item in Attached.ToArray())
        {
            item.Changed?.Invoke();
        }

        CheckVisibility();
    }

    private static void Watch()
    {
        foreach (var visual in Watched)
        {
            visual.PropertyChanged -= OnVisibilityChanged;
            visual.DetachedFromVisualTree -= OnDetached;
            if (visual is ItemsControl list)
            {
                list.LayoutUpdated -= OnLayout;
            }
        }

        Watched.Clear();
        foreach (var item in Attached.Where(item => item.Ordering).ToArray())
        {
            if (item.List is not { } list)
            {
                continue;
            }

            foreach (var visual in list.GetSelfAndVisualAncestors())
            {
                if (Watched.Add(visual))
                {
                    visual.PropertyChanged += OnVisibilityChanged;
                    visual.DetachedFromVisualTree += OnDetached;
                }
            }

            list.LayoutUpdated -= OnLayout;
            list.LayoutUpdated += OnLayout;
        }
    }

    private static void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == ItemsControl.ItemsSourceProperty)
        {
            CheckVisibility();
        }
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => CheckVisibility();

    private static void OnLayout(object? sender, EventArgs e)
    {
        foreach (var item in Attached.Where(item => item.Ordering).ToArray())
        {
            item.Changed?.Invoke();
        }
    }

    private static void CheckVisibility()
    {
        if (_checking || Session.Row is null)
        {
            return;
        }

        _checking = true;
        Dispatcher.UIThread.Post(() =>
        {
            _checking = false;
            if (Session.Row is not null && !Attached.Any(item => item.Ordering && item._item.IsEffectivelyVisible))
            {
                Session.Cancel();
            }
            else
            {
                Watch();
            }
        }, DispatcherPriority.Loaded);
    }
}
