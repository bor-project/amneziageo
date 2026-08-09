using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Строка сервера на главном экране: нажатие выбирает конфигурацию, свайп влево открывает «Изменить»
/// и «Удалить», свайп вправо - «Подключить»; пульт и клавиатура открывают их стрелками.
/// </summary>
internal sealed partial class ServerRow : UserControl
{
    // Under this travel the gesture is still a tap; past it, and past the vertical travel, it is a swipe.
    private const double SwipeThreshold = 12;

    // Bare space left between the moved row and the buttons it uncovers.
    private const double SwipeGap = 8;

    private readonly TranslateTransform _shift = new();
    private readonly TranslateTransform _nameShift = new();
    private readonly ListReorder _reorder;
    private Point _origin;
    private bool _pressed;
    private bool _swiping;
    private bool _settled;

    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(SelectCommand));

    public static readonly StyledProperty<ICommand?> ConnectCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(ConnectCommand));

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(EditCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(DeleteCommand));

    public static readonly StyledProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(DropCommand));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ServerRow, bool>(nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsConnectOpenProperty =
        AvaloniaProperty.Register<ServerRow, bool>(nameof(IsConnectOpen), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// ctor
    /// </summary>
    public ServerRow()
    {
        InitializeComponent();
        _reorder = new ListReorder(this, vertical: true) { HoldFirst = true };
        HiddenTip.Watch(Actions());
        FacePart.RenderTransform = _shift;
        TextPart.RenderTransform = _nameShift;

        // The row is never dragged by hand: every change of X is run by this transition, short enough to keep up
        // with the finger and eased out so it lands instead of stopping. The name rides the same timing.
        _shift.Transitions = Glide();
        _nameShift.Transitions = Glide();

        static Transitions Glide() => new()
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(170),
                Easing = new CubicEaseOut(),
            },
        };

        // Tunnelled: the gesture is read before the row button treats it as a press.
        AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnRowMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnRowCaptureLost, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Команда нажатия по строке.
    /// </summary>
    public ICommand? SelectCommand
    {
        get => GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    /// <summary>
    /// Команда кнопки «Подключить».
    /// </summary>
    public ICommand? ConnectCommand
    {
        get => GetValue(ConnectCommandProperty);
        set => SetValue(ConnectCommandProperty, value);
    }

    /// <summary>
    /// Команда кнопки «Изменить».
    /// </summary>
    public ICommand? EditCommand
    {
        get => GetValue(EditCommandProperty);
        set => SetValue(EditCommandProperty, value);
    }

    /// <summary>
    /// Команда подтверждённого удаления.
    /// </summary>
    public ICommand? DeleteCommand
    {
        get => GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    /// <summary>
    /// Команда, принимающая новый порядок строк.
    /// </summary>
    public ICommand? DropCommand
    {
        get => GetValue(DropCommandProperty);
        set => SetValue(DropCommandProperty, value);
    }

    /// <summary>
    /// Открыты ли кнопки строки.
    /// </summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>
    /// Открыта ли кнопка подключения.
    /// </summary>
    public bool IsConnectOpen
    {
        get => GetValue(IsConnectOpenProperty);
        set => SetValue(IsConnectOpenProperty, value);
    }

    // How far the row slides: past the buttons it uncovers, margins and gap included, so no button keeps a
    // strip of the row over it - a tap there would close the row instead of running the command.
    private double ActionsWidth => Uncovered(ActionsPart);

    private double ConnectWidth => Uncovered(ConnectPart);

    private static double Uncovered(Control part)
    {
        var width = part.Bounds.Width > 0 ? part.Bounds.Width : part.DesiredSize.Width;
        return width + part.Margin.Left + part.Margin.Right + SwipeGap;
    }

    // Which side the row stands off: -1 the edit and delete pair, 1 the connect button, 0 neither.
    private int Side => IsOpen ? -1 : IsConnectOpen ? 1 : 0;

    // Where the row stands for the side it is on.
    private double Offset => IsOpen ? -ActionsWidth : IsConnectOpen ? ConnectWidth : 0;

    // Free width beside the name and the address inside their column: how far they can follow the row back.
    private double NameSlack =>
        Math.Max(0, TextPart.Bounds.Width - Math.Max(NamePart.DesiredSize.Width, EndpointPart.DesiredSize.Width));

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty || change.Property == IsConnectOpenProperty)
        {
            Slide(Offset);
            HiddenTip.Drop([FacePart, .. Actions()]);
        }
        else if (change.Property == DropCommandProperty)
        {
            _reorder.Dropped = DropCommand;
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // No swipe on a remote: the right arrow uncovers the edit and delete pair, the left arrow the connect
        // button, and the opposite arrow puts an open row back, seating focus on what it uncovers.
        if (e.Key == Key.Right && IsConnectOpen)
        {
            Settle(0);
            FacePart.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && !IsOpen)
        {
            Settle(-1);
            FirstButton(ActionsPart)?.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && IsOpen)
        {
            Settle(0);
            FacePart.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && !IsConnectOpen)
        {
            Settle(1);
            FirstButton(ConnectPart)?.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        // The row answers a press on its own face and nothing else. The strip a swipe uncovers is the buttons'
        // - glyphs, the space around them and the gap beside them alike - so a press aimed at a button neither
        // reaches the row nor undoes the swipe, however the press is read.
        var point = e.GetPosition(this);
        if (Bared(ActionsPart, IsOpen, point) || Bared(ConnectPart, IsConnectOpen, point)
            || !Contains(FacePart, e.Source))
        {
            return;
        }

        // An open row takes the first tap to close itself, so a stray press cannot pick or connect it.
        if (Side != 0)
        {
            Settle(0);
            e.Handled = true;
            return;
        }

        _pressed = true;
        _swiping = false;
        _settled = false;
        _origin = point;
        _reorder.Press(e);
    }

    // Whether the press landed on the strip an open side bares: the buttons' own box, widened by the gap the
    // row keeps off them.
    private static bool Bared(Control part, bool open, Point point)
    {
        return open && point.X >= part.Bounds.X - SwipeGap && point.X <= part.Bounds.Right + SwipeGap;
    }

    private void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        // A row already on its way to another place in the list keeps the pointer.
        if (_reorder.Dragging)
        {
            _reorder.Move(e);
            e.Handled = true;
            return;
        }

        var point = e.GetPosition(this);
        var dx = point.X - _origin.X;
        if (Math.Abs(dx) < SwipeThreshold || Math.Abs(dx) <= Math.Abs(point.Y - _origin.Y))
        {
            // Nothing across the row: the finger may be carrying it up or down the list instead.
            if (_reorder.Move(e))
            {
                e.Handled = true;
            }

            return;
        }

        _reorder.Cancel();

        if (!_swiping)
        {
            _swiping = true;
            // Taking the pointer drops the row button's press, so the swipe never ends in a pick; it also keeps
            // the list from scrolling under the finger.
            e.Pointer.Capture(this);
        }

        // One move per swipe, and the row runs the whole way without waiting for the finger to cover the
        // distance: an open row only goes back, and only under a finger heading for the buttons it bares; the
        // side it stands off opens only from a closed row. Nothing opens behind a close.
        if (!_settled)
        {
            var side = Side == 0 ? (dx < 0 ? -1 : 1) : Math.Sign(dx) == -Side ? 0 : Side;
            if (side != Side)
            {
                _settled = true;
                Settle(side);
            }
        }

        e.Handled = true;
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_reorder.Release())
        {
            _pressed = false;
            e.Handled = true;
            return;
        }

        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        _settled = false;
        if (!_swiping)
        {
            return;
        }

        _swiping = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // The list took the pointer mid-swipe: leave the row where its state says.
    private void OnRowCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _reorder.Cancel();
        _pressed = false;
        _settled = false;
        if (_swiping)
        {
            _swiping = false;
            Slide(Offset);
        }
    }

    private void Settle(int side)
    {
        IsOpen = side < 0;
        IsConnectOpen = side > 0;
        Slide(Offset);
        if (side != 0)
        {
            CloseOthers();
        }
    }

    // The buttons both sides of the row keep under it.
    private List<Button> Actions()
    {
        return [.. ActionsPart.Children.OfType<Button>(), .. ConnectPart.Children.OfType<Button>()];
    }

    private static Button? FirstButton(Panel part)
    {
        return part.Children.OfType<Button>().FirstOrDefault(b => b.IsVisible);
    }

    private void Slide(double shift)
    {
        _shift.X = shift;

        // The name walks against a row leaving to the left and keeps its place on screen, as far as the space
        // beside it goes; a row leaving to the right uncovers nothing over it, so the name rides along.
        _nameShift.X = shift < 0 ? Math.Min(-shift, NameSlack) : 0;
    }

    // Only one row keeps its buttons uncovered.
    private void CloseOthers()
    {
        if (this.FindAncestorOfType<ItemsControl>()?.ItemsSource is not { } rows)
        {
            return;
        }

        foreach (var row in rows.OfType<ConfigItemViewModel>())
        {
            if (!ReferenceEquals(row, DataContext))
            {
                row.SwipeOpen = false;
                row.ConnectOpen = false;
            }
        }
    }

    private static bool Contains(Visual root, object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, root))
            {
                return true;
            }
        }

        return false;
    }
}
