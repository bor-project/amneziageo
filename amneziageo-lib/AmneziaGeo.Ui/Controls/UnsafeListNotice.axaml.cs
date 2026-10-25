using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Плашка списка, который ведёт через VPN всё, кроме выбранного.
/// </summary>
internal sealed partial class UnsafeListNotice : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<UnsafeListNotice, string?>(nameof(Text));

    /// <summary>
    /// ctor
    /// </summary>
    public UnsafeListNotice()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Почему список опасен и что с ним сделать.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
