using Xunit;
using System.Collections.ObjectModel;
using AmneziaGeo.Ui.Controls;
using Avalonia.Input;

namespace AmneziaGeo.Tests;

public sealed class ReorderSessionTests
{
    [Fact]
    public void Movement_stays_in_preview_until_committed()
    {
        var rows = MakeRows();
        var before = rows.ToArray();
        var saves = 0;
        var session = new ReorderSession<object>();
        session.Begin(rows, rows[0], () => saves++);
        Assert.Equal(2, session.Move(2, 2));
        Assert.Equal(new[] { before[1], before[2], before[0], before[3] }, rows);
        Assert.Equal(0, saves);
        Assert.Equal(2, session.Commit());
        Assert.Equal(1, saves);
        session.Commit();
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Cancel_restores_order_without_saving()
    {
        var rows = MakeRows();
        var before = rows.ToArray();
        var saves = 0;
        var session = new ReorderSession<object>();
        session.Begin(rows, rows[0], () => saves++);
        session.Move(1, 1);
        session.Move(1, 1);
        Assert.Equal(0, session.Cancel());
        Assert.Equal(before, rows);
        Assert.Equal(0, saves);
        Assert.Null(session.Row);
    }

    [Fact]
    public void Returning_to_original_position_does_not_save()
    {
        var rows = MakeRows();
        var saves = 0;
        var session = new ReorderSession<object>();
        session.Begin(rows, rows[1], () => saves++);
        session.Move(1, 1);
        session.Move(-1, 1);
        session.Commit();
        Assert.Equal(0, saves);
    }

    [Theory]
    [InlineData(0, -1, 1, false)]
    [InlineData(3, 1, 1, false)]
    [InlineData(0, 1, 1, true)]
    [InlineData(0, -1, 2, false)]
    [InlineData(1, 1, 2, false)]
    [InlineData(2, -1, 2, false)]
    [InlineData(1, -1, 2, true)]
    [InlineData(0, 2, 2, true)]
    [InlineData(2, -2, 2, true)]
    [InlineData(2, 2, 2, false)]
    [InlineData(2, 3, 2, false)]
    [InlineData(2, 0, 2, false)]
    public void Movement_respects_grid_edges(int at, int delta, int columns, bool allowed)
    {
        var rows = MakeRows();
        var session = new ReorderSession<object>();
        session.Begin(rows, rows[at], () => { });
        Assert.Equal(allowed, session.CanMove(delta, columns));
        Assert.Equal(allowed ? at + delta : -1, session.Move(delta, columns));
        session.Cancel();
    }

    [Fact]
    public void Taking_another_list_cancels_previous_preview()
    {
        var first = MakeRows();
        var before = first.ToArray();
        var second = MakeRows();
        var session = new ReorderSession<object>();
        session.Begin(first, first[0], () => Assert.Fail("Unexpected save"));
        session.Move(1, 1);
        session.Begin(second, second[0], () => { });
        Assert.Equal(before, first);
        Assert.Same(second, session.Rows);
        session.Cancel();
    }

    [Fact]
    public void Authoritative_refresh_ends_preview_without_saving()
    {
        var rows = MakeRows();
        var session = new ReorderSession<object>();
        session.Begin(rows, rows[0], () => Assert.Fail("Unexpected save"));
        session.Move(1, 1);
        rows.Clear();
        Assert.Null(session.Row);
        session.Commit();
    }

    [Theory]
    [InlineData(Key.Left, 1, 0)]
    [InlineData(Key.Right, 1, 0)]
    [InlineData(Key.Up, 1, -1)]
    [InlineData(Key.Down, 1, 1)]
    [InlineData(Key.Left, 2, -1)]
    [InlineData(Key.Right, 2, 1)]
    [InlineData(Key.Up, 2, -2)]
    [InlineData(Key.Down, 2, 2)]
    public void Arrows_follow_current_column_count(Key key, int columns, int expected)
    {
        Assert.Equal(expected, CardGesture.Step(key, columns));
    }

    private static ObservableCollection<object> MakeRows() => [new(), new(), new(), new()];
}
