using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using AmneziaGeo.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Auto-switching section: whether the default route follows a live server, in what order the servers are walked
/// and which of them are passed over.
/// </summary>
internal sealed partial class FailoverViewModel : ViewModelBase
{
    // Minutes the priority server answers before the route goes back to it, taken when the return is switched on.
    private const int DefaultReturnMinutes = 5;

    private readonly Func<string, string, Task> _push;
    private readonly Func<IReadOnlyList<string>, Task<bool>> _order;

    // Set while the snapshot seeds the rows and the switches; suppresses their save.
    private bool _seeding;

    // Order the list was left in, held until the snapshot catches up with it.
    private IReadOnlyList<string>? _pendingOrder;

    // Whether the row being moved has left its place.
    private bool _moved;

    /// <summary>
    /// ctor
    /// </summary>
    public FailoverViewModel(Func<string, string, Task> push, Func<IReadOnlyList<string>, Task<bool>> order)
    {
        _push = push;
        _order = order;
        Rows.CollectionChanged += OnRowsChanged;
    }

    /// <summary>
    /// Servers in the order auto-switching walks them.
    /// </summary>
    public ObservableCollection<FailoverRowViewModel> Rows { get; } = [];

    /// <summary>
    /// Whether the default route moves to the next server when the one carrying it stops answering.
    /// </summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>
    /// Whether the route goes back to a server higher in the list once it answers again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReturnMinutesEnabled))]
    private bool _returnEnabled;

    /// <summary>
    /// Minutes the higher server answers before the route goes back to it.
    /// </summary>
    [ObservableProperty]
    private int _returnMinutes = DefaultReturnMinutes;

    /// <summary>
    /// The minutes input is editable only while the return is on.
    /// </summary>
    public bool ReturnMinutesEnabled => ReturnEnabled;

    /// <summary>
    /// Seeds the switches and the rows from the agent snapshot.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        _seeding = true;
        try
        {
            Enabled = snapshot.FailoverEnabled;
            ReturnEnabled = snapshot.FailoverReturnMinutes > 0;
            if (snapshot.FailoverReturnMinutes > 0)
            {
                ReturnMinutes = snapshot.FailoverReturnMinutes;
            }

            ApplyRows(snapshot);
        }
        finally
        {
            _seeding = false;
        }
    }

    /// <summary>
    /// Moves the row by one place.
    /// </summary>
    internal void MoveRow(FailoverRowViewModel row, int delta)
    {
        var from = Rows.IndexOf(row);
        var to = from + delta;
        if (from >= 0 && to >= 0 && to < Rows.Count)
        {
            Rows.Move(from, to);
        }
    }

    /// <summary>
    /// Enters the move mode on the row, or leaves it. Only one row is moved at a time.
    /// </summary>
    internal void ToggleMove(FailoverRowViewModel row)
    {
        if (row.IsMoving)
        {
            EndMove(row);
            return;
        }

        foreach (var other in Rows)
        {
            other.IsMoving = false;
        }

        _moved = false;
        row.IsMoving = true;
    }

    /// <summary>
    /// Leaves the move mode, storing the order the row was left in.
    /// </summary>
    internal void EndMove(FailoverRowViewModel row)
    {
        if (!row.IsMoving)
        {
            return;
        }

        row.IsMoving = false;
        if (_moved)
        {
            _moved = false;
            SaveOrder();
        }
    }

    /// <summary>
    /// Sends the agent the order the rows now stand in.
    /// </summary>
    internal void SaveOrder()
    {
        var names = Rows.Select(row => row.Name).ToList();
        _pendingOrder = names;
        _ = StoreOrderAsync(names);
    }

    /// <summary>
    /// Sends the agent the servers auto-switching passes over.
    /// </summary>
    internal void PushSkipped()
    {
        if (!_seeding)
        {
            _ = _push(SettingKeys.FailoverSkipped, NameList.Join(Rows.Where(row => !row.Participates).Select(row => row.Name)));
        }
    }

    // A refused order lets the next snapshot put the rows back where the agent keeps them.
    private async Task StoreOrderAsync(IReadOnlyList<string> names)
    {
        if (!await _order(names) && ReferenceEquals(_pendingOrder, names))
        {
            _pendingOrder = null;
        }
    }

    // Keeps the rows on the configurations the agent reports, in their order; a row survives while its name does,
    // so a checkbox under the pointer does not jump on every snapshot. An order the agent has not echoed yet holds
    // its place, and only while the same names are in play: a removal drops it and reseeds.
    private void ApplyRows(StatusSnapshot snapshot)
    {
        var skipped = NameList.Split(snapshot.FailoverSkipped).ToHashSet(StringComparer.Ordinal);
        var names = snapshot.Configs.Select(entry => entry.Name).ToList();
        var hold = _pendingOrder is { } pending
            && !pending.SequenceEqual(names, StringComparer.Ordinal)
            && pending.Count == names.Count
            && new HashSet<string>(pending, StringComparer.Ordinal).SetEquals(names);
        if (!hold)
        {
            _pendingOrder = null;
            if (!Rows.Select(row => row.Name).SequenceEqual(names, StringComparer.Ordinal))
            {
                Rows.Clear();
                foreach (var name in names)
                {
                    Rows.Add(new FailoverRowViewModel(name, this));
                }
            }
        }

        for (var index = 0; index < Rows.Count; index++)
        {
            var row = Rows[index];
            row.Number = index + 1;
            row.IsCurrent = string.Equals(snapshot.DefaultRouteHeld, row.Name, StringComparison.Ordinal);
            row.Participates = !skipped.Contains(row.Name);
        }
    }

    // Numbers follow the places; an order set here waits for the agent to echo it back.
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (var index = 0; index < Rows.Count; index++)
        {
            Rows[index].Number = index + 1;
        }

        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            _moved = true;
            _pendingOrder = Rows.Select(row => row.Name).ToList();
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_seeding)
        {
            _ = _push(SettingKeys.FailoverEnabled, value ? "on" : "off");
        }
    }

    partial void OnReturnEnabledChanged(bool value)
    {
        if (!_seeding)
        {
            PushReturn();
        }
    }

    partial void OnReturnMinutesChanged(int value)
    {
        if (!_seeding && value > 0)
        {
            PushReturn();
        }
    }

    // Zero minutes leave the route where it is, so the switch and the number ride one setting.
    private void PushReturn()
    {
        var minutes = ReturnEnabled && ReturnMinutes > 0 ? ReturnMinutes : 0;
        _ = _push(SettingKeys.FailoverReturnMinutes, minutes.ToString(CultureInfo.InvariantCulture));
    }
}
