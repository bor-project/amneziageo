using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Предупреждение списка, который ведёт через VPN все приложения или весь их UDP.
/// </summary>
internal sealed partial class ListWarning : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ListWarning, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ListWarning, string?>(nameof(Text));

    /// <summary>
    /// ctor
    /// </summary>
    public ListWarning()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Кто идёт через VPN.
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Почему это важно и что с этим сделать.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
