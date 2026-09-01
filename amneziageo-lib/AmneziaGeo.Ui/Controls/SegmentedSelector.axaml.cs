using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Общий сегментный переключатель одной из 2-4 взаимоисключающих опций.
/// </summary>
internal sealed partial class SegmentedSelector : UserControl
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<SegmentedSelector, ICommand?>(nameof(Command));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SegmentedSelector, string?>(nameof(Label));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<SegmentedSelector, string?>(nameof(Hint));

    public static readonly StyledProperty<bool> HeadingProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Heading));

    public static readonly StyledProperty<HorizontalAlignment> AlignProperty =
        AvaloniaProperty.Register<SegmentedSelector, HorizontalAlignment>(nameof(Align), HorizontalAlignment.Left);

    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(IsCompact));

    public static readonly StyledProperty<bool> DenseProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Dense));

    public static readonly StyledProperty<string?> Item1TextProperty =
        AvaloniaProperty.Register<SegmentedSelector, string?>(nameof(Item1Text));

    public static readonly StyledProperty<object?> Item1ParamProperty =
        AvaloniaProperty.Register<SegmentedSelector, object?>(nameof(Item1Param));

    public static readonly StyledProperty<bool> Item1ActiveProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Item1Active));

    public static readonly StyledProperty<bool> Item1EnabledProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Item1Enabled), true);

    public static readonly StyledProperty<Geometry?> Item1IconProperty =
        AvaloniaProperty.Register<SegmentedSelector, Geometry?>(nameof(Item1Icon));

    public static readonly StyledProperty<string?> Item2TextProperty =
        AvaloniaProperty.Register<SegmentedSelector, string?>(nameof(Item2Text));

    public static readonly StyledProperty<object?> Item2ParamProperty =
        AvaloniaProperty.Register<SegmentedSelector, object?>(nameof(Item2Param));

    public static readonly StyledProperty<bool> Item2ActiveProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Item2Active));

    public static readonly StyledProperty<bool> Item2EnabledProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Item2Enabled), true);

    public static readonly StyledProperty<Geometry?> Item2IconProperty =
        AvaloniaProperty.Register<SegmentedSelector, Geometry?>(nameof(Item2Icon));

    public static readonly StyledProperty<string?> Item3TextProperty =
        AvaloniaProperty.Register<SegmentedSelector, string?>(nameof(Item3Text));

    public static readonly StyledProperty<object?> Item3ParamProperty =
        AvaloniaProperty.Register<SegmentedSelector, object?>(nameof(Item3Param));

    public static readonly StyledProperty<bool> Item3ActiveProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Item3Active));

    public static readonly StyledProperty<bool> Item3EnabledProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Item3Enabled), true);

    public static readonly StyledProperty<Geometry?> Item3IconProperty =
        AvaloniaProperty.Register<SegmentedSelector, Geometry?>(nameof(Item3Icon));

    public static readonly StyledProperty<string?> Item4TextProperty =
        AvaloniaProperty.Register<SegmentedSelector, string?>(nameof(Item4Text));

    public static readonly StyledProperty<object?> Item4ParamProperty =
        AvaloniaProperty.Register<SegmentedSelector, object?>(nameof(Item4Param));

    public static readonly StyledProperty<bool> Item4ActiveProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Item4Active));

    public static readonly StyledProperty<bool> Item4EnabledProperty =
        AvaloniaProperty.Register<SegmentedSelector, bool>(nameof(Item4Enabled), true);

    public static readonly StyledProperty<Geometry?> Item4IconProperty =
        AvaloniaProperty.Register<SegmentedSelector, Geometry?>(nameof(Item4Icon));

    /// <summary>
    /// ctor
    /// </summary>
    public SegmentedSelector()
    {
        InitializeComponent();
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>
    /// Подаёт подпись как заголовок раздела, а не как строку внутри него.
    /// </summary>
    public bool Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public HorizontalAlignment Align
    {
        get => GetValue(AlignProperty);
        set => SetValue(AlignProperty, value);
    }

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    /// <summary>
    /// Сжимает кнопки: четыре подписи со значками встают в одну строку.
    /// </summary>
    public bool Dense
    {
        get => GetValue(DenseProperty);
        set => SetValue(DenseProperty, value);
    }

    public string? Item1Text
    {
        get => GetValue(Item1TextProperty);
        set => SetValue(Item1TextProperty, value);
    }

    public object? Item1Param
    {
        get => GetValue(Item1ParamProperty);
        set => SetValue(Item1ParamProperty, value);
    }

    public bool Item1Active
    {
        get => GetValue(Item1ActiveProperty);
        set => SetValue(Item1ActiveProperty, value);
    }

    public bool Item1Enabled
    {
        get => GetValue(Item1EnabledProperty);
        set => SetValue(Item1EnabledProperty, value);
    }

    /// <summary>
    /// Значок слева от подписи.
    /// </summary>
    public Geometry? Item1Icon
    {
        get => GetValue(Item1IconProperty);
        set => SetValue(Item1IconProperty, value);
    }

    public string? Item2Text
    {
        get => GetValue(Item2TextProperty);
        set => SetValue(Item2TextProperty, value);
    }

    public object? Item2Param
    {
        get => GetValue(Item2ParamProperty);
        set => SetValue(Item2ParamProperty, value);
    }

    public bool Item2Active
    {
        get => GetValue(Item2ActiveProperty);
        set => SetValue(Item2ActiveProperty, value);
    }

    public bool Item2Enabled
    {
        get => GetValue(Item2EnabledProperty);
        set => SetValue(Item2EnabledProperty, value);
    }

    /// <summary>
    /// Значок слева от подписи.
    /// </summary>
    public Geometry? Item2Icon
    {
        get => GetValue(Item2IconProperty);
        set => SetValue(Item2IconProperty, value);
    }

    public string? Item3Text
    {
        get => GetValue(Item3TextProperty);
        set => SetValue(Item3TextProperty, value);
    }

    public object? Item3Param
    {
        get => GetValue(Item3ParamProperty);
        set => SetValue(Item3ParamProperty, value);
    }

    public bool Item3Active
    {
        get => GetValue(Item3ActiveProperty);
        set => SetValue(Item3ActiveProperty, value);
    }

    public bool Item3Enabled
    {
        get => GetValue(Item3EnabledProperty);
        set => SetValue(Item3EnabledProperty, value);
    }

    /// <summary>
    /// Значок слева от подписи.
    /// </summary>
    public Geometry? Item3Icon
    {
        get => GetValue(Item3IconProperty);
        set => SetValue(Item3IconProperty, value);
    }

    public string? Item4Text
    {
        get => GetValue(Item4TextProperty);
        set => SetValue(Item4TextProperty, value);
    }

    public object? Item4Param
    {
        get => GetValue(Item4ParamProperty);
        set => SetValue(Item4ParamProperty, value);
    }

    public bool Item4Active
    {
        get => GetValue(Item4ActiveProperty);
        set => SetValue(Item4ActiveProperty, value);
    }

    public bool Item4Enabled
    {
        get => GetValue(Item4EnabledProperty);
        set => SetValue(Item4EnabledProperty, value);
    }

    /// <summary>
    /// Значок слева от подписи.
    /// </summary>
    public Geometry? Item4Icon
    {
        get => GetValue(Item4IconProperty);
        set => SetValue(Item4IconProperty, value);
    }
}
