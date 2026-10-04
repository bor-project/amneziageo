using System.Collections.ObjectModel;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Блок «Работа в фоне»: ограничения, которые называет платформа.
/// </summary>
internal sealed class BackgroundLimitsViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<BackgroundLimit>> _read;
    private readonly Action<BackgroundLimitKind> _open;

    /// <summary>
    /// ctor
    /// </summary>
    public BackgroundLimitsViewModel(Func<IReadOnlyList<BackgroundLimit>> read, Action<BackgroundLimitKind> open)
    {
        _read = read;
        _open = open;
        Reload();
    }

    /// <summary>
    /// Строки блока.
    /// </summary>
    public ObservableCollection<BackgroundLimitRow> Rows { get; } = [];

    /// <summary>
    /// Есть ли что показать.
    /// </summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>
    /// Перечитывает ограничения у платформы.
    /// </summary>
    public void Reload()
    {
        var limits = _read();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!limits.Any(limit => limit.Kind == Rows[i].Kind))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < limits.Count; i++)
        {
            var limit = limits[i];
            if (Rows.FirstOrDefault(row => row.Kind == limit.Kind) is { } known)
            {
                known.State = limit.State;
            }
            else
            {
                Rows.Insert(Math.Min(i, Rows.Count), new BackgroundLimitRow(limit.Kind, limit.State, _open));
            }
        }

        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>
    /// Перечитывает подписи строк на новом языке.
    /// </summary>
    public void Relabel()
    {
        foreach (var row in Rows)
        {
            row.Relabel();
        }
    }
}
