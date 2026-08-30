using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Карточка каталога маршрутизации: имя, состав списка, флаг применения и кнопка настроек. На телевизоре
/// пульт входит в карточку, ходит по её контролам сверху вниз и выходит «назад»; перетаскивание
/// переставляет карточку.
/// </summary>
internal sealed partial class RoutingCard : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<RoutingCard, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<RoutingCard, ICommand?>(nameof(SelectCommand));

    public static readonly StyledProperty<ICommand?> PickCommandProperty =
        AvaloniaProperty.Register<RoutingCard, ICommand?>(nameof(PickCommand));

    public static readonly StyledProperty<ActionSheetViewModel?> SheetProperty =
        AvaloniaProperty.Register<RoutingCard, ActionSheetViewModel?>(nameof(Sheet));

    public static readonly StyledProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.Register<RoutingCard, ICommand?>(nameof(DropCommand));

    // Удержание центральной кнопки, открывающее меню карточки.
    private static readonly TimeSpan MenuHold = TimeSpan.FromMilliseconds(600);

    private readonly ListReorder<RoutingListSummaryViewModel> _reorder;

    private readonly DispatcherTimer _press;

    private bool _entered;

    private bool _holding;

    private bool _menued;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingCard()
    {
        InitializeComponent();
        _reorder = new ListReorder<RoutingListSummaryViewModel>(this, vertical: false);
        _press = new DispatcherTimer { Interval = MenuHold };
        _press.Tick += OnPressHeld;

        // Тело карточки берёт фокус только на телевизоре: там оно - вход в её контролы.
        FacePart.Focusable = UiPlatform.IsTelevision;
        ApplyStopFocus();

        // Тоннельно: жест читается раньше, чем его возьмут контролы карточки.
        AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnCardMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnCardReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnCardCaptureLost, RoutingStrategies.Tunnel);
        GotFocus += OnCardGotFocus;

        if (UiPlatform.IsTelevision)
        {
            AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnCardKeyUp, RoutingStrategies.Tunnel);
            LostFocus += OnCardLostFocus;
        }
    }

    /// <summary>
    /// Команда кнопки «Настройки».
    /// </summary>
    public ICommand? OpenCommand
    {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    /// <summary>
    /// Команда флага применения.
    /// </summary>
    public ICommand? SelectCommand
    {
        get => GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    /// <summary>
    /// Команда, отмечающая карточку выбранной в каталоге.
    /// </summary>
    public ICommand? PickCommand
    {
        get => GetValue(PickCommandProperty);
        set => SetValue(PickCommandProperty, value);
    }

    /// <summary>
    /// Набор способов оболочки: им выносится меню карточки.
    /// </summary>
    public ActionSheetViewModel? Sheet
    {
        get => GetValue(SheetProperty);
        set => SetValue(SheetProperty, value);
    }

    // Место в карточке, откуда пульт ушёл на перестановку.
    private enum Stop
    {
        Face,
        Action,
        Settings,
    }

    /// <summary>
    /// Команда, принимающая новый порядок карточек.
    /// </summary>
    public ICommand? DropCommand
    {
        get => GetValue(DropCommandProperty);
        set => SetValue(DropCommandProperty, value);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyOrderLook();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DropCommandProperty)
        {
            _reorder.Dropped = DropCommand;
        }
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        Pick();
        _reorder.Press(e);
    }

    private void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (_reorder.Move(e))
        {
            e.Handled = true;
        }
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_reorder.Release())
        {
            e.Handled = true;
        }
    }

    private void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _reorder.Cancel();
    }

    // Открывает настройки карточки - то же, что кнопка в подвале.
    private void Open()
    {
        if (DataContext is { } item && OpenCommand?.CanExecute(item) == true)
        {
            OpenCommand.Execute(item);
        }
    }

    private void OnCardGotFocus(object? sender, GotFocusEventArgs e)
    {
        Pick();
    }

    // Карточка, по которой кликнули или в которую вошёл фокус, становится выбранной в каталоге.
    private void Pick()
    {
        if (DataContext is { } item && PickCommand?.CanExecute(item) == true)
        {
            PickCommand.Execute(item);
        }
    }

    // Центральная кнопка вводит пульт в карточку, стрелки водят по её контролам, «назад» выводит; пока пульт
    // внутри, стрелка не уходит на соседнюю карточку.
    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (_reorder.Ordering)
        {
            OnOrderKey(e);
            return;
        }

        if (e.Key is Key.Enter or Key.Space && !_entered)
        {
            if (!_holding)
            {
                _holding = true;
                _menued = false;
                _press.Start();
            }

            e.Handled = true;
            return;
        }

        if (!_entered)
        {
            return;
        }

        if (e.Key is Key.Escape)
        {
            Leave();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            Step(e.Key is Key.Down ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            // Поперёк контролов пульт из карточки не уходит.
            e.Handled = true;
        }
    }

    // Короткое нажатие вводит пульт в карточку, долгое уже открыло меню. Отпускание гасится: иначе оно
    // нажимает контрол, на который только что сел фокус.
    private void OnCardKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_holding || e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        _holding = false;
        _press.Stop();
        if (!_menued)
        {
            Enter();
        }

        _menued = false;
        e.Handled = true;
    }

    // Удержание центральной кнопки выносит меню карточки.
    private void OnPressHeld(object? sender, EventArgs e)
    {
        _press.Stop();
        if (!_holding || DataContext is not RoutingListSummaryViewModel item)
        {
            return;
        }

        _menued = true;
        CardMenu.Present(FacePart, Sheet, item.Name, Open, Take);
    }

    // Берёт карточку в перестановку: дальше её водят стрелки, а пульт остаётся на теле.
    private void Take()
    {
        if (_reorder.Hold())
        {
            ApplyOrderLook();
            FacePart.Focus(NavigationMethod.Directional);
        }
    }

    // Пока карточка взята: стрелки её двигают, центральная кнопка фиксирует, «назад» возвращает порядок.
    private void OnOrderKey(KeyEventArgs e)
    {
        var step = CardGesture.Step(e.Key, _reorder.Columns());
        if (step != 0)
        {
            Carry(step);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            Settle(undo: false);
            e.Handled = true;
        }
        else if (e.Key is Key.Escape)
        {
            Settle(undo: true);
            e.Handled = true;
        }
    }

    // Двигает карточку и возвращает на неё фокус: перестановка пересобирает её контейнер.
    private void Carry(int delta)
    {
        var list = _reorder.List;
        var stop = Focused();
        Land(list, _reorder.Nudge(delta), stop);
    }

    // Отпускает взятую карточку, записав порядок или вернув прежний.
    private void Settle(bool undo)
    {
        var list = _reorder.List;
        var at = undo ? _reorder.Undo() : _reorder.Fix();
        Opacity = 1;
        Land(list, at, Stop.Face);
    }

    private Stop Focused()
    {
        if (ActivePart.IsKeyboardFocusWithin)
        {
            return Stop.Action;
        }

        return SettingsPart.IsKeyboardFocusWithin ? Stop.Settings : Stop.Face;
    }

    // Взятая в перестановку карточка гаснет так же, как перетаскиваемая.
    private void ApplyOrderLook()
    {
        Opacity = _reorder.Ordering ? 0.6 : 1;
    }

    // Сажает пульт на то же место карточки, вставшей на новое место каталога.
    private static void Land(ItemsControl? list, int index, Stop stop)
    {
        if (list is null || index < 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (list.ContainerFromIndex(index) is not Visual slot
                    || slot.GetSelfAndVisualDescendants().OfType<RoutingCard>().FirstOrDefault() is not { } card)
                {
                    return;
                }

                var part = stop switch
                {
                    Stop.Action => (Control)card.ActivePart,
                    Stop.Settings => card.SettingsPart,
                    _ => card.FacePart,
                };
                part.Focus(NavigationMethod.Directional);
            },
            DispatcherPriority.Loaded);
    }

    // Фокус ушёл на сторону: карточка перестаёт держать пульт внутри себя.
    private void OnCardLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsKeyboardFocusWithin || !_entered)
            {
                return;
            }

            _entered = false;
            ApplyStopFocus();
        });
    }

    private void Enter()
    {
        _entered = true;
        ApplyStopFocus();
        ActivePart.Focus(NavigationMethod.Directional);
    }

    private void Leave()
    {
        _entered = false;
        ApplyStopFocus();
        FacePart.Focus(NavigationMethod.Directional);
    }

    // Водит по контролам карточки, останавливаясь на краях.
    private void Step(int delta)
    {
        var stops = new Control[] { ActivePart, SettingsPart };
        var at = Array.FindIndex(stops, stop => stop.IsKeyboardFocusWithin);
        var next = at < 0 ? 0 : at + delta;
        if (next >= 0 && next < stops.Length)
        {
            stops[next].Focus(NavigationMethod.Directional);
        }
    }

    // Пока пульт не вошёл в карточку, её контролы не берут фокус: стрелка ходит по карточкам, а не по кнопкам.
    private void ApplyStopFocus()
    {
        var stop = _entered || !UiPlatform.IsTelevision;
        ActivePart.Focusable = stop;
        SettingsPart.Focusable = stop;
    }
}
