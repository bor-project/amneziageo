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
/// Строка сервера на главном экране: нажатие подключает конфигурацию, свайп влево открывает «Изменить»
/// и «Удалить», пульт и клавиатура открывают их стрелкой вправо.
/// </summary>
internal sealed partial class ServerRow : UserControl
{
    // Under this travel the gesture is still a tap; past it, and past the vertical travel, it is a swipe.
    private const double SwipeThreshold = 12;

    // Bare space left between the moved row and the buttons it uncovers.
    private const double SwipeGap = 8;

    private readonly TranslateTransform _shift = new();
    private Point _origin;
    private bool _pressed;
    private bool _swiping;

    public static readonly StyledProperty<ICommand?> ConnectCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(ConnectCommand));

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(EditCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<ServerRow, ICommand?>(nameof(DeleteCommand));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ServerRow, bool>(nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// ctor
    /// </summary>
    public ServerRow()
    {
        InitializeComponent();
        FacePart.RenderTransform = _shift;

        // The row is never dragged by hand: every change of X is run by this transition, short enough to keep up
        // with the finger and eased out so it lands instead of stopping.
        _shift.Transitions = new Transitions
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

    // How far the row slides: past the pair it uncovers, margins and gap included, so no button keeps a strip
    // of the row over it - a tap there would close the row instead of running the command.
    private double ActionsWidth
    {
        get
        {
            var width = ActionsPart.Bounds.Width > 0 ? ActionsPart.Bounds.Width : ActionsPart.DesiredSize.Width;
            return width + ActionsPart.Margin.Left + ActionsPart.Margin.Right + SwipeGap;
        }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            Slide(IsOpen ? -ActionsWidth : 0);
        }
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // No swipe on a remote: the right arrow uncovers the buttons and seats focus on the first one, the left
        // arrow puts them back. A closed row keeps both arrows for directional navigation.
        if (e.Key == Key.Right && !IsOpen)
        {
            Settle(true);
            ActionsPart.Children.OfType<Button>().FirstOrDefault(b => b.IsVisible)?.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && IsOpen)
        {
            Settle(false);
            FacePart.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        // The uncovered strip is the buttons', glyphs and the space around them alike: a press there never
        // closes the row, so a finger that misses a glyph does not undo the swipe instead.
        if (IsOpen && e.GetPosition(this).X >= Bounds.Width - ActionsWidth)
        {
            return;
        }

        // A press on the uncovered buttons is theirs.
        if (Contains(ActionsPart, e.Source))
        {
            return;
        }

        // An open row takes the first tap to close itself, so a stray press cannot connect it.
        if (IsOpen)
        {
            Settle(false);
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
            // Taking the pointer drops the row button's press, so the swipe never ends in a connect; it also
            // keeps the list from scrolling under the finger.
            e.Pointer.Capture(this);
        }

        // The direction alone decides: the row runs the whole way to the buttons, or the whole way back, without
        // waiting for the finger to cover the distance.
        var open = dx < 0;
        if (open != IsOpen)
        {
            Settle(open);
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
            Slide(IsOpen ? -ActionsWidth : 0);
        }
    }

    private void Settle(bool open)
    {
        IsOpen = open;
        Slide(open ? -ActionsWidth : 0);
        if (open)
        {
            CloseOthers();
        }
    }

    private void Slide(double shift)
    {
        _shift.X = shift;
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
