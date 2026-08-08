using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Карточка раздела с одним тумблером: иконка, заголовок с описанием, тумблер и строка о том,
/// действует ли выбранное сейчас.
/// </summary>
internal sealed partial class ApplySwitchCard : UserControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<ApplySwitchCard, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ApplySwitchCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<ApplySwitchCard, string?>(nameof(Description));

    public static readonly StyledProperty<string?> OnTextProperty =
        AvaloniaProperty.Register<ApplySwitchCard, string?>(nameof(OnText));

    public static readonly StyledProperty<string?> OffTextProperty =
        AvaloniaProperty.Register<ApplySwitchCard, string?>(nameof(OffText));

    public static readonly StyledProperty<string?> OnNoticeProperty =
        AvaloniaProperty.Register<ApplySwitchCard, string?>(nameof(OnNotice));

    public static readonly StyledProperty<string?> OffNoticeProperty =
        AvaloniaProperty.Register<ApplySwitchCard, string?>(nameof(OffNotice));

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<ApplySwitchCard, bool>(nameof(IsChecked), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> SwitchEnabledProperty =
        AvaloniaProperty.Register<ApplySwitchCard, bool>(nameof(SwitchEnabled), true);

    public static readonly StyledProperty<bool> ShowNoticeProperty =
        AvaloniaProperty.Register<ApplySwitchCard, bool>(nameof(ShowNotice));

    public static readonly StyledProperty<bool> ShowOnNoticeProperty =
        AvaloniaProperty.Register<ApplySwitchCard, bool>(nameof(ShowOnNotice));

    public static readonly StyledProperty<bool> ShowOffNoticeProperty =
        AvaloniaProperty.Register<ApplySwitchCard, bool>(nameof(ShowOffNotice));

    /// <summary>
    /// ctor
    /// </summary>
    public ApplySwitchCard()
    {
        InitializeComponent();
        RefreshNotice();
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? OnText
    {
        get => GetValue(OnTextProperty);
        set => SetValue(OnTextProperty, value);
    }

    public string? OffText
    {
        get => GetValue(OffTextProperty);
        set => SetValue(OffTextProperty, value);
    }

    public string? OnNotice
    {
        get => GetValue(OnNoticeProperty);
        set => SetValue(OnNoticeProperty, value);
    }

    public string? OffNotice
    {
        get => GetValue(OffNoticeProperty);
        set => SetValue(OffNoticeProperty, value);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public bool SwitchEnabled
    {
        get => GetValue(SwitchEnabledProperty);
        set => SetValue(SwitchEnabledProperty, value);
    }

    /// <summary>
    /// Whether the divider and one of the notices are shown.
    /// </summary>
    public bool ShowNotice => GetValue(ShowNoticeProperty);

    /// <summary>
    /// Whether the on-state notice is shown.
    /// </summary>
    public bool ShowOnNotice => GetValue(ShowOnNoticeProperty);

    /// <summary>
    /// Whether the off-state notice is shown.
    /// </summary>
    public bool ShowOffNotice => GetValue(ShowOffNoticeProperty);

    /// <summary>
    /// Тумблер карточки: цель направленного фокуса с соседних контролов.
    /// </summary>
    public ToggleSwitch Toggle => SwitchPart;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty
            || change.Property == OnNoticeProperty
            || change.Property == OffNoticeProperty)
        {
            RefreshNotice();
        }
    }

    // Picks the notice that matches the switch position.
    private void RefreshNotice()
    {
        var on = IsChecked && !string.IsNullOrEmpty(OnNotice);
        var off = !IsChecked && !string.IsNullOrEmpty(OffNotice);
        SetValue(ShowOnNoticeProperty, on);
        SetValue(ShowOffNoticeProperty, off);
        SetValue(ShowNoticeProperty, on || off);
    }

    // Tapping the label toggles the switch, so the whole row is the target, not the knob alone.
    private void OnLabelTapped(object? sender, TappedEventArgs e)
    {
        if (SwitchEnabled)
        {
            IsChecked = !IsChecked;
        }
    }
}
