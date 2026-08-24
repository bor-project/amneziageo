using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Строка списка автопереключения: ручка перемещения, номер, имя, метка держателя маршрута и участие в
/// переключении. Мышь и палец тянут строку на новое место, клавиатура и пульт двигают её режимом перемещения.
/// </summary>
internal sealed partial class FailoverRow : UserControl
{
    private readonly ListReorder<FailoverRowViewModel> _reorder;

    /// <summary>
    /// ctor
    /// </summary>
    public FailoverRow()
    {
        InitializeComponent();
        _reorder = new ListReorder<FailoverRowViewModel>(this, vertical: true);

        // Тоннельно: жест и стрелки читаются раньше, чем их возьмут контролы строки.
        AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnRowMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnRowCaptureLost, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
    }

    private FailoverRowViewModel? Row => DataContext as FailoverRowViewModel;

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _reorder.Dropped = Row?.DropCommand;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Перестановка пересобирает контейнер: фокус едет за строкой, которую ведут.
        if (Row is { IsMoving: true })
        {
            GripPart.Focus(NavigationMethod.Directional);
        }
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        _reorder.Press(e);
    }

    private void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (_reorder.Move(e))
        {
            e.Handled = true;
        }
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_reorder.Release())
        {
            e.Handled = true;
        }
    }

    private void OnRowCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _reorder.Cancel();
    }

    // Пока строку ведут, стрелки двигают её саму, а не ходят по списку; ОК и «назад» фиксируют порядок.
    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Row is not { IsMoving: true } row)
        {
            return;
        }

        if (e.Key is Key.Up or Key.Down)
        {
            row.Step(e.Key is Key.Down ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space or Key.Escape)
        {
            row.EndMove();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            // Поперёк списка строка не уезжает.
            e.Handled = true;
        }
    }
}
