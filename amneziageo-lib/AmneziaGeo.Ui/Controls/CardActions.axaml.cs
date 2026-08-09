using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Кнопки «Изменить» и «Удалить» карточки сервера: на широком экране стоят на её лице, в компакте - на
/// полосе под ней, которую открывает свайп.
/// </summary>
internal sealed partial class CardActions : UserControl
{
    public static readonly StyledProperty<double> ButtonWidthProperty =
        AvaloniaProperty.Register<CardActions, double>(nameof(ButtonWidth), 52);

    public static readonly StyledProperty<double> ButtonHeightProperty =
        AvaloniaProperty.Register<CardActions, double>(nameof(ButtonHeight), double.NaN);

    /// <summary>
    /// ctor
    /// </summary>
    public CardActions()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Ширина кнопки.
    /// </summary>
    public double ButtonWidth
    {
        get => GetValue(ButtonWidthProperty);
        set => SetValue(ButtonWidthProperty, value);
    }

    /// <summary>
    /// Высота кнопки; без неё кнопка тянется на всю высоту карточки.
    /// </summary>
    public double ButtonHeight
    {
        get => GetValue(ButtonHeightProperty);
        set => SetValue(ButtonHeightProperty, value);
    }

    /// <summary>
    /// Кнопки в порядке слева направо.
    /// </summary>
    public IEnumerable<Button> Items => Part.Children.OfType<Button>();
}
