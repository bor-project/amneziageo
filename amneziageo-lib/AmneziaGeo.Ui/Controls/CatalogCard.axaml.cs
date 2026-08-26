using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Карточка каталога настроек: имя, кнопка подключения, замер и настройки. На телевизоре пульт входит
/// в карточку, ходит по её кнопкам и выходит «назад»; перетаскивание переставляет карточку, двойной щелчок
/// открывает её настройки.
/// </summary>
internal sealed partial class CatalogCard : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> ConnectCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(ConnectCommand));

    public static readonly StyledProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(DropCommand));

    public static readonly StyledProperty<ICommand?> PickCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(PickCommand));

    public static readonly StyledProperty<ICommand?> DefaultCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(DefaultCommand));

    public static readonly StyledProperty<ICommand?> PrimaryCommandProperty =
        AvaloniaProperty.Register<CatalogCard, ICommand?>(nameof(PrimaryCommand));

    private readonly ListReorder<ConfigItemViewModel> _reorder;

    private bool _entered;

    private bool _entering;

    /// <summary>
    /// ctor
    /// </summary>
    public CatalogCard()
    {
        InitializeComponent();
        _reorder = new ListReorder<ConfigItemViewModel>(this, vertical: false);

        // Тело карточки берёт фокус: на телевизоре оно вход в её контролы, на прочих - область приоритета.
        FacePart.Focusable = true;
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
        else
        {
            AddHandler(KeyDownEvent, OnCardMoveKey, RoutingStrategies.Tunnel);
        }
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Перестановка пересобирает контейнер: фокус едет за карточкой, которую ведут.
        if (DataContext is ConfigItemViewModel { IsMoving: true })
        {
            FacePart.Focus(NavigationMethod.Directional);
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

    /// <summary>
    /// Команда тумблера «По умолчанию».
    /// </summary>
    public ICommand? DefaultCommand
    {
        get => GetValue(DefaultCommandProperty);
        set => SetValue(DefaultCommandProperty, value);
    }

    /// <summary>
    /// Команда кнопки «Основной».
    /// </summary>
    public ICommand? PrimaryCommand
    {
        get => GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
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
        if (CardGesture.OpensSettings(FacePart, e))
        {
            _reorder.Cancel();
            Open();
            e.Handled = true;
            return;
        }

        _reorder.Press(e);
    }

    // Двойной щелчок по телу карточки открывает её настройки - то же, что кнопка в подвале.
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
            Step(e.Key is Key.Down ? 1 : -1, 0);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            // Поперёк пульт ходит по кнопкам строки и из карточки не уходит.
            Step(0, e.Key is Key.Right ? 1 : -1);
            e.Handled = true;
        }
    }

    // Enter по телу вводит карточку в режим перемещения, стрелки двигают её по приоритету, Enter и Esc
    // режим закрывают и складывают порядок.
    private void OnCardMoveKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ConfigItemViewModel item)
        {
            return;
        }

        if (!item.IsMoving)
        {
            if (e.Key is Key.Enter && FacePart.IsFocused)
            {
                item.IsMoving = true;
                e.Handled = true;
            }

            return;
        }

        if (e.Key is Key.Up or Key.Left or Key.Down or Key.Right)
        {
            _reorder.Step(e.Key is Key.Down or Key.Right ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space or Key.Escape)
        {
            item.IsMoving = false;
            if (DropCommand?.CanExecute(null) == true)
            {
                DropCommand.Execute(null);
            }

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
        Rows()[0][0].Focus(NavigationMethod.Directional);
    }

    private void Leave()
    {
        _entering = false;
        _entered = false;
        ApplyStopFocus();
        FacePart.Focus(NavigationMethod.Directional);
    }

    // Водит по контролам карточки: вниз-вверх между строками, вправо-влево по кнопкам строки, на краях стоит.
    private void Step(int rows, int cols)
    {
        var grid = Rows();
        var row = grid.FindIndex(line => line.Exists(stop => stop.IsKeyboardFocusWithin));
        if (row < 0)
        {
            grid[0][0].Focus(NavigationMethod.Directional);
            return;
        }

        var nextRow = row + rows;
        if (nextRow < 0 || nextRow >= grid.Count)
        {
            return;
        }

        var col = grid[row].FindIndex(stop => stop.IsKeyboardFocusWithin);
        var nextCol = Math.Clamp(col + cols, 0, grid[nextRow].Count - 1);
        grid[nextRow][nextCol].Focus(NavigationMethod.Directional);
    }

    // Контролы карточки строками: роль и подключение сверху, тумблер и настройки ниже; запертую кнопку
    // туннеля и спрятанный тумблер пульт пропускает.
    private List<List<Control>> Rows()
    {
        var rows = new List<List<Control>>();
        var head = new List<Control>();
        if (PrimaryPart.IsVisible)
        {
            head.Add(PrimaryPart);
        }

        if (ConnectPart is { IsVisible: true, IsEnabled: true })
        {
            head.Add(ConnectPart);
        }

        if (head.Count > 0)
        {
            rows.Add(head);
        }

        if (DefaultPart.IsVisible)
        {
            rows.Add([DefaultPart]);
        }

        rows.Add([SettingsPart]);
        return rows;
    }

    // Пока пульт не вошёл в карточку, её контролы не берут фокус: стрелка ходит по карточкам, а не по кнопкам.
    private void ApplyStopFocus()
    {
        var stop = _entered || !UiPlatform.IsTelevision;
        PrimaryPart.Focusable = stop;
        ConnectPart.Focusable = stop;
        DefaultPart.Focusable = stop;
        SettingsPart.Focusable = stop;
    }
}
