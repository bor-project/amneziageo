using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Строка настройки: подпись слева, тумблер справа, описание под подписью.
/// </summary>
internal sealed partial class SettingSwitchRow : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SettingSwitchRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingSwitchRow, string?>(nameof(Description));

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<SettingSwitchRow, bool>(nameof(IsChecked), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> SwitchEnabledProperty =
        AvaloniaProperty.Register<SettingSwitchRow, bool>(nameof(SwitchEnabled), true);

    /// <summary>
    /// ctor
    /// </summary>
    public SettingSwitchRow()
    {
        InitializeComponent();
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
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

    // Tapping the label toggles the switch, so the whole row is the target, not the knob alone.
    private void OnLabelTapped(object? sender, TappedEventArgs e)
    {
        if (SwitchEnabled)
        {
            IsChecked = !IsChecked;
        }
    }
}
