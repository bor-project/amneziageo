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
    private Point _origin;
    private bool _pressed;
    private bool _swiping;

    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(SelectCommand));

    public static readonly StyledProperty<ICommand?> ConnectCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(ConnectCommand));

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(EditCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(DeleteCommand));

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
        // The uncovered strip is the buttons', glyphs and the space around them alike: a press there never
        // closes the row, so a finger that misses a glyph does not undo the swipe instead.
        var x = e.GetPosition(this).X;
        if ((IsOpen && x >= Bounds.Width - ActionsWidth) || (IsConnectOpen && x <= ConnectWidth))
        {
            return;
        }

        // A press on the uncovered buttons is theirs.
        if (Contains(ActionsPart, e.Source) || Contains(ConnectPart, e.Source))
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
        _origin = e.GetPosition(this);
    }

    private void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var dx = point.X - _origin.X;
        if (Math.Abs(dx) < SwipeThreshold || Math.Abs(dx) <= Math.Abs(point.Y - _origin.Y))
        {
            return;
        }

        if (!_swiping)
        {
            _swiping = true;
            // Taking the pointer drops the row button's press, so the swipe never ends in a pick; it also keeps
            // the list from scrolling under the finger.
            e.Pointer.Capture(this);
        }

        // The direction alone decides: the row runs the whole way to the buttons it heads for, or the whole way
        // back over the ones it stands off, without waiting for the finger to cover the distance.
        var side = dx < 0 ? (Side == 1 ? 0 : -1) : (Side == -1 ? 0 : 1);
        if (side != Side)
        {
            Settle(side);
        }

        // A reversal within the same gesture counts from here.
        _origin = point;
        e.Handled = true;
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
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
        _pressed = false;
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
