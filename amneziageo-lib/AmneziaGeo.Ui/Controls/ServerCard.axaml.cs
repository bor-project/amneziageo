using System;
using System.Collections.Generic;
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
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Карточка сервера: имя, адрес, замер и кнопки на лице. На узком экране «Изменить» и «Удалить» уходят под
/// карточку, их открывает свайп влево. Нажатие выбирает конфигурацию, Enter вводит пульт внутрь, «назад»
/// выводит.
/// </summary>
internal sealed partial class ServerCard : UserControl
{
    // Under this travel the gesture is still a tap; past it, and past the vertical travel, it is a swipe.
    private const double SwipeThreshold = 12;

    // Bare space left between the moved card and the buttons it uncovers.
    private const double SwipeGap = 8;

    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<ServerCard, ICommand?>(nameof(SelectCommand));

    public static readonly StyledProperty<ICommand?> ConnectCommandProperty =
        AvaloniaProperty.Register<ServerCard, ICommand?>(nameof(ConnectCommand));

    public static readonly StyledProperty<ICommand?> EditCommandProperty =
        AvaloniaProperty.Register<ServerCard, ICommand?>(nameof(EditCommand));

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<ServerCard, ICommand?>(nameof(DeleteCommand));

    public static readonly StyledProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.Register<ServerCard, ICommand?>(nameof(DropCommand));

    public static readonly StyledProperty<bool> EnteredProperty =
        AvaloniaProperty.Register<ServerCard, bool>(nameof(Entered));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ServerCard, bool>(nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<ServerCard, bool>(nameof(Compact));

    private readonly TranslateTransform _shift = new();
    private readonly TranslateTransform _nameShift = new();
    private readonly ListReorder _reorder;
    private Point _origin;
    private bool _pressed;
    private bool _swiping;
    private bool _settled;

    /// <summary>
    /// ctor
    /// </summary>
    public ServerCard()
    {
        InitializeComponent();
        _reorder = new ListReorder(this, vertical: false);
        HiddenTip.Watch(StripActions.Items);
        HiddenTip.Watch(FaceActions.Items);
        FacePart.RenderTransform = _shift;
        NamePart.RenderTransform = _nameShift;
        EndpointPart.RenderTransform = _nameShift;
        ProbePart.RenderTransform = _nameShift;

        // The card is never dragged by hand across the buttons: every change of X is run by this transition,
        // short enough to keep up with the finger and eased out so it lands instead of stopping. The lines ride
        // the same timing.
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

        // Tunnelled: the gesture and the remote's centre key are read before the card's own button takes them.
        AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnCardMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnCardReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnCardCaptureLost, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Tunnel);
        LostFocus += OnCardLostFocus;

        // A card built already open knows how far to stand off only once its buttons have been measured.
        SizeChanged += (_, _) => Slide(Offset);
    }

    /// <summary>
    /// Команда нажатия по карточке.
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
    /// Команда, принимающая новый порядок карточек.
    /// </summary>
    public ICommand? DropCommand
    {
        get => GetValue(DropCommandProperty);
        set => SetValue(DropCommandProperty, value);
    }

    /// <summary>
    /// Вошёл ли пульт внутрь карточки: только тогда её кнопки берут фокус.
    /// </summary>
    public bool Entered
    {
        get => GetValue(EnteredProperty);
        set => SetValue(EnteredProperty, value);
    }

    /// <summary>
    /// Открыты ли кнопки под карточкой.
    /// </summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>
    /// Растянута ли карточка на узкий экран: тогда кнопка подключения крупнее, а «Изменить» и «Удалить» уходят
    /// под карточку.
    /// </summary>
    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>
    /// Выводит пульт из карточки, в которой он стоит. Возвращает, была ли такая карточка.
    /// </summary>
    public static bool LeaveEntered(IInputElement? focused)
    {
        for (var visual = focused as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ServerCard { Entered: true } card)
            {
                card.Leave();
                return true;
            }
        }

        return false;
    }

    // How far the card slides: past the buttons it uncovers, gap included, so no button keeps a strip of the
    // card over it - a tap there would close the card instead of running the command.
    private double ActionsWidth =>
        (ActionsBox.Bounds.Width > 0 ? ActionsBox.Bounds.Width : ActionsBox.DesiredSize.Width) + SwipeGap;

    // Where the card stands for the state it is in.
    private double Offset => IsOpen ? -ActionsWidth : 0;

    // Free width beside the three lines: how far they can follow the card back.
    private double NameSlack
    {
        get
        {
            var text = Math.Max(
                NamePart.DesiredSize.Width,
                Math.Max(EndpointPart.DesiredSize.Width, ProbePart.DesiredSize.Width));
            return Math.Max(0, NamePart.Bounds.Width - text);
        }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            Slide(Offset);
            HiddenTip.Drop([FacePart, ConnectPart, .. StripActions.Items]);
        }
        else if (change.Property == DropCommandProperty)
        {
            _reorder.Dropped = DropCommand;
        }
        else if (change.Property == CompactProperty)
        {
            // Only the narrow card swipes, and only there does the drag have to keep off that axis.
            _reorder.HoldFirst = Compact;
        }
    }

    // The centre key steps into the card and uncovers its buttons, the arrows walk them, and the back key steps
    // out; the card holds the remote until then, so an arrow inside it never lands on the card next door.
    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && !Entered)
        {
            Enter();
            e.Handled = true;
            return;
        }

        if (!Entered)
        {
            return;
        }

        if (e.Key is Key.Escape)
        {
            Leave();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            Step(e.Key == Key.Right ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
        }
    }

    // Focus taken elsewhere leaves the card behind: its buttons stop taking focus and go back under it.
    private void OnCardLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Entered && !IsKeyboardFocusWithin)
            {
                Entered = false;
                Settle(false);
            }
        });
    }

    private void Enter()
    {
        Entered = true;
        Settle(Compact);
        ConnectPart.Focus(NavigationMethod.Directional);
    }

    private void Leave()
    {
        Entered = false;
        Settle(false);
        FacePart.Focus(NavigationMethod.Directional);
    }

    // Walks the buttons the card carries, stopping at either end.
    private void Step(int delta)
    {
        var buttons = Buttons();
        var at = buttons.FindIndex(button => button.IsKeyboardFocusWithin);
        var next = at < 0 ? 0 : at + delta;
        if (next >= 0 && next < buttons.Count)
        {
            buttons[next].Focus(NavigationMethod.Directional);
        }
    }

    private List<Button> Buttons()
    {
        return [ConnectPart, .. Actions().Where(button => button.IsVisible)];
    }

    // The pair the card shows in the layout it stands in.
    private IEnumerable<Button> Actions()
    {
        return Compact ? StripActions.Items : FaceActions.Items;
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        // The card answers a press on its own face and nothing else. The strip a swipe uncovers is the buttons'
        // - glyphs, the space around them and the gap beside them alike - so a press aimed at a button neither
        // reaches the card nor undoes the swipe.
        var point = e.GetPosition(this);
        if (Bared(point) || !Contains(FacePart, e.Source))
        {
            return;
        }

        // An open card takes the first tap to close itself, so a stray press cannot pick it.
        if (IsOpen)
        {
            Settle(false);
            e.Handled = true;
            return;
        }

        _pressed = true;
        _swiping = false;
        _settled = false;
        _origin = point;
        _reorder.Press(e);
    }

    // Whether the press landed on the strip an open card bares: the buttons' own box, widened by the gap the
    // card keeps off them.
    private bool Bared(Point point)
    {
        return IsOpen && point.X >= ActionsBox.Bounds.X - SwipeGap && point.X <= ActionsBox.Bounds.Right + SwipeGap;
    }

    private void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        // A card already on its way to another place in the list keeps the pointer.
        if (_reorder.Dragging)
        {
            _reorder.Move(e);
            e.Handled = true;
            return;
        }

        // Nothing across the card, or a card that keeps its buttons on the face and has nothing to uncover: the
        // pointer may be carrying it to another place in the list instead.
        var point = e.GetPosition(this);
        var dx = point.X - _origin.X;
        if (!Compact || Math.Abs(dx) < SwipeThreshold || Math.Abs(dx) <= Math.Abs(point.Y - _origin.Y))
        {
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
            // Taking the pointer drops the card button's press, so the swipe never ends in a pick.
            e.Pointer.Capture(this);
        }

        // One move per swipe, and the card runs the whole way without waiting for the finger to cover the
        // distance: it uncovers the buttons to the left and puts them back to the right.
        if (!_settled)
        {
            var open = dx < 0;
            if (open != IsOpen)
            {
                _settled = true;
                Settle(open);
            }
        }

        e.Handled = true;
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
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

    // The list took the pointer mid-swipe: leave the card where its state says.
    private void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e)
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

    private void Settle(bool open)
    {
        IsOpen = open;
        Slide(Offset);
        if (open)
        {
            CloseOthers();
        }
    }

    private void Slide(double shift)
    {
        _shift.X = shift;

        // The lines walk against a card leaving to the left and keep their place on screen, as far as the space
        // beside them goes.
        _nameShift.X = Math.Min(-shift, NameSlack);
    }

    // Only one card keeps its buttons uncovered.
    private void CloseOthers()
    {
        if (this.FindAncestorOfType<ItemsControl>()?.ItemsSource is not { } cards)
        {
            return;
        }

        foreach (var card in cards.OfType<ConfigItemViewModel>())
        {
            if (!ReferenceEquals(card, DataContext))
            {
                card.SwipeOpen = false;
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
