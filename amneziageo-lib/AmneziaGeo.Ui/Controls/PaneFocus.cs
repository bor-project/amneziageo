using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Переходы фокуса между панелями настроек. Направленная навигация выбирает цель по геометрии, поэтому на
/// широком экране пульт уходит из содержимого в меню разделов и обратно не возвращается (#201).
/// </summary>
internal static class PaneFocus
{
    /// <summary>
    /// Ставит фокус на первый контрол ветки: сверху вниз, слева направо.
    /// </summary>
    public static bool FocusFirst(Visual? root)
    {
        if (root is null)
        {
            return false;
        }

        var target = Focusables(root)
            .OrderBy(item => Math.Round(item.Area.Y / 12))
            .ThenBy(item => item.Area.X)
            .Select(item => item.Control)
            .FirstOrDefault();

        return target?.Focus(NavigationMethod.Directional) == true;
    }

    /// <summary>
    /// Первая показанная ветка из перечисленных.
    /// </summary>
    public static Visual? Shown(params Visual?[] roots) =>
        roots.FirstOrDefault(root => root is { IsEffectivelyVisible: true });

    /// <summary>
    /// Есть ли в ветке фокусируемый контрол с этой стороны от заданного.
    /// </summary>
    public static bool HasNeighbour(Visual root, Visual from, NavigationDirection direction)
    {
        if (from is not Control origin || Place(origin, root) is not { } start)
        {
            return false;
        }

        foreach (var (control, area) in Focusables(root))
        {
            if (ReferenceEquals(control, origin) || control.IsVisualAncestorOf(origin) || origin.IsVisualAncestorOf(control))
            {
                continue;
            }

            var found = direction switch
            {
                NavigationDirection.Left => area.Right <= start.X + 1 && Crosses(area.Y, area.Bottom, start.Y, start.Bottom),
                NavigationDirection.Right => area.X + 1 >= start.Right && Crosses(area.Y, area.Bottom, start.Y, start.Bottom),
                NavigationDirection.Up => area.Bottom <= start.Y + 1 && Crosses(area.X, area.Right, start.X, start.Right),
                NavigationDirection.Down => area.Y + 1 >= start.Bottom && Crosses(area.X, area.Right, start.X, start.Right),
                _ => false,
            };

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(Control Control, Rect Area)> Focusables(Visual root)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (!control.Focusable || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled)
            {
                continue;
            }

            if (Place(control, root) is { } area)
            {
                yield return (control, area);
            }
        }
    }

    private static Rect? Place(Control control, Visual root) =>
        control.TranslatePoint(default, root) is { } origin ? new Rect(origin, control.Bounds.Size) : null;

    private static bool Crosses(double from, double to, double otherFrom, double otherTo) =>
        from < otherTo && otherFrom < to;
}
