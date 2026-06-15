namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A routing-list pick exposed to combo boxes. Id is null for the synthetic "none" choice.
/// </summary>
internal sealed record RoutingListChoice(long? Id, string Name)
{
    /// <summary>
    /// The synthetic "no routing list" choice.
    /// </summary>
    public static RoutingListChoice None { get; } = new(null, "— не задан —");

    /// <summary>
    /// True for the synthetic "none" choice.
    /// </summary>
    public bool IsNone => Id is null;
}
