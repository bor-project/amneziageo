using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Ряд плашек режимов на карточке: один вид и один размер у всякого каталога.
/// </summary>
internal sealed partial class CardTags : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<CardTag>?> TagsProperty =
        AvaloniaProperty.Register<CardTags, IReadOnlyList<CardTag>?>(nameof(Tags));

    public static readonly StyledProperty<bool> StopsProperty =
        AvaloniaProperty.Register<CardTags, bool>(nameof(Stops), true);

    /// <summary>
    /// ctor
    /// </summary>
    public CardTags()
    {
        InitializeComponent();
        Strip.ContainerPrepared += (_, _) => Dispatcher.UIThread.Post(ApplyStops, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Плашки в порядке показа: хвост уходит за край первым.
    /// </summary>
    public IReadOnlyList<CardTag>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    /// <summary>
    /// Берут ли плашки фокус: на телевизоре пульт входит в них вместе с карточкой.
    /// </summary>
    public bool Stops
    {
        get => GetValue(StopsProperty);
        set => SetValue(StopsProperty, value);
    }

    /// <summary>
    /// Плашки, отвечающие на нажатие, в порядке показа; ушедшие за край в счёт не идут.
    /// </summary>
    public IReadOnlyList<Control> Presses() =>
        [.. Strip.GetVisualDescendants().OfType<Button>().Where(press => Pressable(press) && Shown(press))];

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StopsProperty || change.Property == TagsProperty)
        {
            Dispatcher.UIThread.Post(ApplyStops, DispatcherPriority.Loaded);
        }
    }

    private static bool Pressable(Control press) => press.DataContext is CardTag { Interactive: true };

    // Плашка за правым краем ряда отсечена клипом, и пульту на ней делать нечего.
    private bool Shown(Visual press) =>
        press.TranslatePoint(default, Strip) is { X: >= 0 } at && at.X < Strip.Bounds.Width;

    private void ApplyStops()
    {
        foreach (var press in Strip.GetVisualDescendants().OfType<Button>())
        {
            press.Focusable = Stops && Pressable(press);
        }
    }
}
