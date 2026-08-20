using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Карточка каталога настроек: имя, адрес, замер, тумблер активности и кнопка настроек. На телевизоре пульт
/// входит в карточку, ходит по её контролам сверху вниз и выходит «назад»; перетаскивание переставляет карточку.
/// </summary>
internal sealed partial class CatalogCard : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(SelectCommand));

    public static readonly StyledProperty<ICommand?> ConnectCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(ConnectCommand));

    public static readonly StyledProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(DropCommand));

    public static readonly StyledProperty<ICommand?> PickCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(PickCommand));

    private readonly ListReorder _reorder;

    private bool _entered;

    private bool _entering;

    /// <summary>
    /// ctor
    /// </summary>
    public CatalogCard()
    {
        InitializeComponent();
        _reorder = new ListReorder(this, vertical: false);

        // Тело карточки берёт фокус только на телевизоре: там оно - вход в её контролы.
        FacePart.Focusable = UiPlatform.IsTelevision;
        ApplyStopFocus();

        // Тоннельно: жест читается раньше, чем его возьмёт кнопка карточки.
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
    /// Команда тумблера активности.
    /// </summary>
    public ICommand? SelectCommand
    {
        get => GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    /// <summary>
    /// Команда кнопки «Подключиться».
    /// </summary>
    public ICommand? ConnectCommand
    {
        get => GetValue(ConnectCommandProperty);
        set => SetValue(ConnectCommandProperty, value);
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
    /// Команда, отмечающая карточку выбранной в каталоге.
    /// </summary>
    public ICommand? PickCommand
    {
        get => GetValue(PickCommandProperty);
        set => SetValue(PickCommandProperty, value);
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

    // Центральная кнопка вводит пульт в карточку, стрелки водят по её контролам, «назад» выводит; пока пульт
    // внутри, стрелка не уходит на соседнюю карточку.
    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && !_entered)
        {
            _entering = true;
            Enter();
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

    // Отпускание ключа входа гасится: иначе оно нажимает контрол, на который только что сел фокус.
    private void OnCardKeyUp(object? sender, KeyEventArgs e)
    {
        if (_entering && e.Key is Key.Enter or Key.Space)
        {
            _entering = false;
            e.Handled = true;
        }
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
        Stops()[0].Focus(NavigationMethod.Directional);
    }

    private void Leave()
    {
        _entering = false;
        _entered = false;
        ApplyStopFocus();
        FacePart.Focus(NavigationMethod.Directional);
    }

    // Водит по контролам карточки, останавливаясь на краях.
    private void Step(int delta)
    {
        var stops = Stops();
        var at = stops.FindIndex(stop => stop.IsKeyboardFocusWithin);
        var next = at < 0 ? 0 : at + delta;
        if (next >= 0 && next < stops.Count)
        {
            stops[next].Focus(NavigationMethod.Directional);
        }
    }

    // Контролы карточки сверху вниз: запертую кнопку туннеля пульт пропускает.
    private List<Control> Stops()
    {
        var stops = new List<Control> { ActivePart };
        if (ConnectPart is { IsVisible: true, IsEnabled: true })
        {
            stops.Add(ConnectPart);
        }

        stops.Add(SettingsPart);
        return stops;
    }

    // Пока пульт не вошёл в карточку, её контролы не берут фокус: стрелка ходит по карточкам, а не по кнопкам.
    private void ApplyStopFocus()
    {
        var stop = _entered || !UiPlatform.IsTelevision;
        ActivePart.Focusable = stop;
        ConnectPart.Focusable = stop;
        SettingsPart.Focusable = stop;
    }
}
