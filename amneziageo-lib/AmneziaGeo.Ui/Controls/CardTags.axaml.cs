using Avalonia;
using Avalonia.Controls;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Ряд плашек режимов на карточке: один вид и один размер у всякого каталога.
/// </summary>
internal sealed partial class CardTags : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<CardTag>?> TagsProperty =
        AvaloniaProperty.Register<CardTags, IReadOnlyList<CardTag>?>(nameof(Tags));

    /// <summary>
    /// ctor
    /// </summary>
    public CardTags()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Плашки в порядке показа: хвост уходит за край первым.
    /// </summary>
    public IReadOnlyList<CardTag>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }
}
