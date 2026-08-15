using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Строка настройки: подпись слева, значение справа, правка в диалоге по нажатию.
/// </summary>
internal sealed partial class SettingValueRow : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SettingValueRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingValueRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingValueRow, string?>(nameof(Description));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<SettingValueRow, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<SettingValueRow, string?>(nameof(Watermark));

    public static readonly StyledProperty<bool> MultilineProperty =
        AvaloniaProperty.Register<SettingValueRow, bool>(nameof(Multiline));

    public static readonly StyledProperty<bool> MonoProperty =
        AvaloniaProperty.Register<SettingValueRow, bool>(nameof(Mono));

    public static readonly StyledProperty<bool> HeadingProperty =
        AvaloniaProperty.Register<SettingValueRow, bool>(nameof(Heading));

    /// <summary>
    /// ctor
    /// </summary>
    public SettingValueRow()
    {
        InitializeComponent();
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// Заголовок диалога правки; пустой берётся из подписи.
    /// </summary>
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

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>
    /// Подсказка в пустом поле диалога.
    /// </summary>
    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>
    /// Держит значение блоком под строкой и правит его многострочным полем.
    /// </summary>
    public bool Multiline
    {
        get => GetValue(MultilineProperty);
        set => SetValue(MultilineProperty, value);
    }

    /// <summary>
    /// Пишет значение моноширинным шрифтом.
    /// </summary>
    public bool Mono
    {
        get => GetValue(MonoProperty);
        set => SetValue(MonoProperty, value);
    }

    /// <summary>
    /// Подаёт подпись как заголовок раздела, а не как строку внутри него.
    /// </summary>
    public bool Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    // Правка идёт в диалоге оболочки: «Отмена» оставляет настройку прежней, чего поле на месте не даёт.
    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (!IsEffectivelyEnabled)
        {
            return;
        }

        var request = new ValueEdit(
            string.IsNullOrEmpty(Title) ? Label : Title,
            Description,
            Watermark,
            Value,
            Multiline,
            Mono);

        var edited = await ValueEditorHost.EditAsync(request);
        if (edited is not null)
        {
            Value = edited;
        }
    }
}
