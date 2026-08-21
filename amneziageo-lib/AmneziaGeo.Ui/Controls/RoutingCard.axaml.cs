using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Карточка каталога маршрутизации: имя, состав списка, флаг применения и кнопка настроек. На телевизоре
/// пульт входит в карточку, ходит по её контролам сверху вниз и выходит «назад».
/// </summary>
internal sealed partial class RoutingCard : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<RoutingCard, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<RoutingCard, ICommand?>(nameof(SelectCommand));

    public static readonly StyledProperty<ICommand?> PickCommandProperty =
        AvaloniaProperty.Register<RoutingCard, ICommand?>(nameof(PickCommand));

    private bool _entered;

    private bool _entering;

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingCard()
    {
        InitializeComponent();

        // Тело карточки берёт фокус только на телевизоре: там оно - вход в её контролы.
        FacePart.Focusable = UiPlatform.IsTelevision;
        ApplyStopFocus();

        // Тоннельно: жест читается раньше, чем его возьмут контролы карточки.
        AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Tunnel);
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

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        Pick();
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
        ActivePart.Focus(NavigationMethod.Directional);
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
