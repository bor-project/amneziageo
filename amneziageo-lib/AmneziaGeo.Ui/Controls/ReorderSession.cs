using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Хранит предварительный порядок карточек.
/// </summary>
internal sealed class ReorderSession<TRow>
    where TRow : class
{
    private List<TRow> _before = [];
    private Action? _save;
    private bool _updating;

    public event Action? Changed;

    public ObservableCollection<TRow>? Rows { get; private set; }

    public TRow? Row { get; private set; }

    public void Begin(ObservableCollection<TRow> rows, TRow row, Action save)
    {
        Cancel();
        if (!rows.Contains(row))
        {
            return;
        }

        Rows = rows;
        Row = row;
        _before = [.. rows];
        _save = save;
        rows.CollectionChanged += OnCollectionChanged;
        Changed?.Invoke();
    }

    public bool CanMove(int delta, int columns)
    {
        if (Rows is not { } rows || Row is not { } row || columns < 1 || delta == 0
            || (Math.Abs(delta) != 1 && Math.Abs(delta) != columns))
        {
            return false;
        }

        var from = rows.IndexOf(row);
        var to = from + delta;
        return from >= 0 && to >= 0 && to < rows.Count
            && (columns == 1 || Math.Abs(delta) != 1 || from / columns == to / columns);
    }

    public int Move(int delta, int columns)
    {
        if (!CanMove(delta, columns))
        {
            return -1;
        }

        var rows = Rows!;
        var from = rows.IndexOf(Row!);
        _updating = true;
        try
        {
            rows.Move(from, from + delta);
        }
        finally
        {
            _updating = false;
        }

        Changed?.Invoke();
        return from + delta;
    }

    public int Commit()
    {
        var at = Rows?.IndexOf(Row!) ?? -1;
        var changed = Rows is { } rows && !rows.SequenceEqual(_before, ReferenceEqualityComparer.Instance);
        var save = _save;
        Clear();
        if (changed)
        {
            save?.Invoke();
        }

        return at;
    }

    public int Cancel()
    {
        Restore();
        var at = Rows?.IndexOf(Row!) ?? -1;
        Clear();
        return at;
    }

    private void Restore()
    {
        if (Rows is not { } rows)
        {
            return;
        }

        var survivors = new Queue<TRow>(_before.Where(rows.Contains));
        var desired = rows.Select(row => _before.Contains(row) ? survivors.Dequeue() : row).ToArray();
        _updating = true;
        try
        {
            for (var i = 0; i < desired.Length; i++)
            {
                var at = rows.IndexOf(desired[i]);
                if (at != i)
                {
                    rows.Move(at, i);
                }
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void Clear()
    {
        if (Rows is { } rows)
        {
            rows.CollectionChanged -= OnCollectionChanged;
        }

        Rows = null;
        Row = null;
        _before = [];
        _save = null;
        Changed?.Invoke();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        Clear();
    }
}
