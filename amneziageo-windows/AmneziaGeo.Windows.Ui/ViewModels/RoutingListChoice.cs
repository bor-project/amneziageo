namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A routing-list pick exposed to combo boxes. Id is null for the synthetic "none" choice.
/// </summary>
internal sealed record RoutingListChoice(long? Id, string Name)
{
    /// <summary>
    /// The synthetic "no routing list" choice.
    /// </summary>
    public static RoutingListChoice None { get; } = new(null, "- не задан -");

    /// <summary>
    /// The synthetic "create a new list" choice (Id -1): picking it reveals the inline new-list editor,
    /// mirroring the "+ Новая конфигурация" sentinel in the config combo.
    /// </summary>
    public static RoutingListChoice NewList { get; } = new(-1, "+ Новый список");

    /// <summary>
    /// True for the synthetic "none" choice.
    /// </summary>
    public bool IsNone => Id is null;

    /// <summary>
    /// True for the synthetic "create a new list" sentinel.
    /// </summary>
    public bool IsNewSentinel => Id == -1;

    /// <summary>
    /// True for a real, persisted list (positive id) - not the "none" or "new" sentinels.
    /// </summary>
    public bool IsReal => Id is > 0;
}
