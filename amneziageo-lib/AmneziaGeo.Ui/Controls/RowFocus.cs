using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Держит фокус в списке, когда строку удалили с клавиатуры или пульта: ставит его на ту же кнопку строки, занявшей
/// место удалённой, а когда строк не осталось, на запасной контрол.
/// </summary>
internal static class RowFocus
{
    /// <summary>
    /// Включает перенос фокуса на списке.
    /// </summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, bool>("Enabled", typeof(RowFocus));

    /// <summary>
    /// Куда уходит фокус, когда строк не осталось: сам контрол или первый контрол его ветки.
    /// </summary>
    public static readonly AttachedProperty<Control?> SpareProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, Control?>("Spare", typeof(RowFocus));

    static RowFocus()
    {
        EnabledProperty.Changed.AddClassHandler<ItemsControl>(OnEnabledChanged);
    }

    public static void SetEnabled(ItemsControl target, bool value) => target.SetValue(EnabledProperty, value);

    public static bool GetEnabled(ItemsControl target) => target.GetValue(EnabledProperty);

    public static void SetSpare(ItemsControl target, Control? value) => target.SetValue(SpareProperty, value);

    public static Control? GetSpare(ItemsControl target) => target.GetValue(SpareProperty);

    private static void OnEnabledChanged(ItemsControl list, AvaloniaPropertyChangedEventArgs e)
    {
        list.RemoveHandler(Button.ClickEvent, OnClick);
        if (e.NewValue is true)
        {
            list.AddHandler(Button.ClickEvent, OnClick, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    // Запоминает строку кнопки, нажатой с клавиатуры или пульта, до того как её команда сработает.
    private static void OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ItemsControl list || e.Source is not Button { Command: { } command } button
            || !button.Classes.Contains(":focus-visible"))
        {
            return;
        }

        var row = button.GetVisualAncestors().OfType<Control>().FirstOrDefault(control => list.IndexFromContainer(control) >= 0);
        if (row is null)
        {
            return;
        }

        var index = list.IndexFromContainer(row);
        var count = list.ItemCount;
        Dispatcher.UIThread.Post(() => Land(list, command, index, count), DispatcherPriority.Loaded);
    }

    // Ставит фокус после того, как строка ушла из списка.
    private static void Land(ItemsControl list, ICommand command, int index, int count)
    {
        var left = list.ItemCount;
        if (left >= count)
        {
            return;
        }

        if (left == 0)
        {
            var spare = GetSpare(list);
            if (spare is { Focusable: true, IsEffectivelyVisible: true })
            {
                spare.Focus(NavigationMethod.Directional);
            }
            else
            {
                PaneFocus.FocusFirst(spare);
            }

            return;
        }

        var target = list.ContainerFromIndex(Math.Min(index, left - 1))?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => ReferenceEquals(button.Command, command) && button.IsEffectivelyVisible);
        target?.Focus(NavigationMethod.Directional);
    }
}
