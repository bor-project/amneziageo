using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Верхнее секционное меню из трёх сегментов, общее для экранов профиля / конфигов / маршрутов.
/// </summary>
internal sealed partial class SectionMenu : UserControl
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<SectionMenu, ICommand?>(nameof(Command));

    public static readonly StyledProperty<string?> Item1TextProperty =
        AvaloniaProperty.Register<SectionMenu, string?>(nameof(Item1Text));

    public static readonly StyledProperty<object?> Item1ParamProperty =
        AvaloniaProperty.Register<SectionMenu, object?>(nameof(Item1Param));

    public static readonly StyledProperty<bool> Item1ActiveProperty =
        AvaloniaProperty.Register<SectionMenu, bool>(nameof(Item1Active));

    public static readonly StyledProperty<bool> Item1EnabledProperty =
        AvaloniaProperty.Register<SectionMenu, bool>(nameof(Item1Enabled), true);

    public static readonly StyledProperty<string?> Item2TextProperty =
        AvaloniaProperty.Register<SectionMenu, string?>(nameof(Item2Text));

    public static readonly StyledProperty<object?> Item2ParamProperty =
        AvaloniaProperty.Register<SectionMenu, object?>(nameof(Item2Param));

    public static readonly StyledProperty<bool> Item2ActiveProperty =
        AvaloniaProperty.Register<SectionMenu, bool>(nameof(Item2Active));

    public static readonly StyledProperty<bool> Item2EnabledProperty =
        AvaloniaProperty.Register<SectionMenu, bool>(nameof(Item2Enabled), true);

    public static readonly StyledProperty<string?> Item3TextProperty =
        AvaloniaProperty.Register<SectionMenu, string?>(nameof(Item3Text));

    public static readonly StyledProperty<object?> Item3ParamProperty =
        AvaloniaProperty.Register<SectionMenu, object?>(nameof(Item3Param));

    public static readonly StyledProperty<bool> Item3ActiveProperty =
        AvaloniaProperty.Register<SectionMenu, bool>(nameof(Item3Active));

    public static readonly StyledProperty<bool> Item3EnabledProperty =
        AvaloniaProperty.Register<SectionMenu, bool>(nameof(Item3Enabled), true);

    /// <summary>
    /// ctor
    /// </summary>
    public SectionMenu()
    {
        InitializeComponent();
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
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
}
