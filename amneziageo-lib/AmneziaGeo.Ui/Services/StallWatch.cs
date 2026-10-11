using System;
using System.Globalization;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Watches how long the interface stays busy after an input and words what was slow for the journal.
/// </summary>
internal sealed class StallWatch(Func<long> clock, Action<Action, int> later, Func<string> place, Action<string> report)
{
    /// <summary>
    /// The pause from which the interface counts as busy, in milliseconds.
    /// </summary>
    public const int SlowMs = 250;

    /// <summary>
    /// How long after an input the interface is watched, in milliseconds.
    /// </summary>
    public const int WatchMs = 3000;

    /// <summary>
    /// The step the interface is asked at, in milliseconds.
    /// </summary>
    public const int BeatMs = 50;

    private bool _watching;
    private long _began;
    private long _beat;
    private int _due;
    private string _input = string.Empty;
    private string _from = string.Empty;
    private string _to = string.Empty;
    private long _longest;
    private int _pauses;

    /// <summary>
    /// Starts a watch at an input, unless one is on.
    /// </summary>
    public void Input(string kind)
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        _began = clock();
        _beat = _began;
        _due = 0;
        _input = kind;
        _from = place();
        _to = _from;
        _longest = 0;
        _pauses = 0;
        later(Beat, 0);
    }

    // Counts the pause since the step before and asks for the next one.
    private void Beat()
    {
        var now = clock();
        var busy = now - _beat - _due;
        if (busy >= SlowMs)
        {
            _pauses++;
            if (busy > _longest)
            {
                _longest = busy;
                _to = place();
            }
        }

        _beat = now;
        if (now - _began < WatchMs)
        {
            _due = BeatMs;
            later(Beat, BeatMs);
            return;
        }

        _watching = false;
        if (_pauses > 0)
        {
            report(Words());
        }
    }

    private string Words()
    {
        var where = string.Equals(_from, _to, StringComparison.Ordinal) ? $"on {_from}" : $"from {_from} to {_to}";
        var count = _pauses > 1 ? $", pauses in {(WatchMs / 1000).ToString(CultureInfo.InvariantCulture)} s: {_pauses.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        return $"window: the interface was busy for {_longest.ToString(CultureInfo.InvariantCulture)} ms after {_input} {where}{count}";
    }
}
