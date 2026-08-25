using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Layout widths on the bootstrap grid, one source for every screen.
/// </summary>
internal sealed partial class UiLayout : ObservableObject
{
    /// <summary>
    /// Bootstrap md: below it the settings screen drops the side-by-side rail + content for a single-column
    /// drilldown.
    /// </summary>
    public const double CompactWidth = 768;

    /// <summary>
    /// Bootstrap sm: below it the section cards stand without their frames.
    /// </summary>
    public const double CardWidth = 576;

    /// <summary>
    /// Bootstrap lg: below it a pane stacks the fields it otherwise keeps side by side.
    /// </summary>
    public const double FieldRowWidth = 992;

    /// <summary>
    /// Bootstrap lg: below it a catalogue stands its cards in one column across the pane.
    /// </summary>
    public const double CardGridWidth = 992;

    /// <summary>
    /// Instance the shared styles bind to.
    /// </summary>
    public static UiLayout Instance { get; } = new();

    /// <summary>
    /// Whether the section cards are drawn without their frames.
    /// </summary>
    [ObservableProperty]
    private bool _isCardless;

    /// <summary>
    /// Gates the card frames by the shell width.
    /// </summary>
    public void Apply(double width)
    {
        IsCardless = width < CardWidth;
    }
}
