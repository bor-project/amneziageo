using System;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Sizes the portions a long list is built in by the time the last one took to lay out, a half or a double of
/// the portion before at most.
/// </summary>
internal sealed class RowPace(Func<long> clock)
{
    /// <summary>
    /// The time a portion is sized to, in milliseconds.
    /// </summary>
    public const int PortionMs = 30;

    /// <summary>
    /// The portion the pace starts with, in rows.
    /// </summary>
    public const int StartRows = 16;

    /// <summary>
    /// The smallest portion, in rows.
    /// </summary>
    public const int MinRows = 16;

    /// <summary>
    /// The largest portion, in rows.
    /// </summary>
    public const int MaxRows = 200;

    private int _built;
    private long _began;

    /// <summary>
    /// The rows the next portion is asked to hold.
    /// </summary>
    public int Rows { get; private set; } = StartRows;

    /// <summary>
    /// Has the next portion built and notes how many rows it came to.
    /// </summary>
    public void Feed(Func<int, int> build)
    {
        _began = clock();
        _built = build(Rows);
    }

    /// <summary>
    /// Sizes the next portion by the time the last one took.
    /// </summary>
    public void Laid()
    {
        if (_built <= 0)
        {
            return;
        }

        var spent = Math.Max(clock() - _began, 1);
        var fit = Math.Clamp(_built * (long)PortionMs / spent, Rows / 2, Rows * 2L);
        Rows = (int)Math.Clamp(fit, MinRows, MaxRows);
        _built = 0;
    }
}
