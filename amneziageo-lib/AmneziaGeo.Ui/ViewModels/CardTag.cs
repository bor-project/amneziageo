using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One mode label on a card: the text, whether the mode is on, and what a press flips.
/// </summary>
internal sealed partial class CardTag : ObservableObject
{
    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _on;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Interactive))]
    private ICommand? _command;

    [ObservableProperty]
    private bool _bad;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string? _hint;

    /// <summary>
    /// ctor
    /// </summary>
    public CardTag(string text, bool on, ICommand? command = null, bool bad = false, bool busy = false, string? hint = null)
    {
        _text = text;
        _on = on;
        _command = command;
        _bad = bad;
        _busy = busy;
        _hint = hint;
    }

    /// <summary>
    /// Отвечает ли плашка на нажатие.
    /// </summary>
    public bool Interactive => Command is not null;

    /// <summary>
    /// Переносит ряд в коллекцию на месте: плашки остаются теми же объектами, поэтому ряд не пересобирается
    /// и нажатая плашка держит фокус.
    /// </summary>
    public static void Sync(ObservableCollection<CardTag> row, IReadOnlyList<CardTag> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < row.Count)
            {
                row[i].Take(wanted[i]);
            }
            else
            {
                row.Add(wanted[i]);
            }
        }

        while (row.Count > wanted.Count)
        {
            row.RemoveAt(row.Count - 1);
        }
    }

    // Принимает состояние вновь собранной плашки.
    private void Take(CardTag other)
    {
        Text = other.Text;
        On = other.On;
        Command = other.Command;
        Bad = other.Bad;
        Busy = other.Busy;
        Hint = other.Hint;
    }
}
