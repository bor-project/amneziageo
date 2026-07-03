using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A routing-list pick exposed to combo boxes. Id is null for the synthetic "none" choice. Creating a list is
/// a button («+ Новый список») in the Routing section, no longer a synthetic combo entry (#111).
/// </summary>
internal sealed record RoutingListChoice(long? Id, string Name)
{
    /// <summary>
    /// The synthetic "no routing list" choice.
    /// </summary>
    public static RoutingListChoice None { get; } = new(null, Loc.Instance.Get("RoutingChoice_None"));

    /// <summary>
    /// True for the synthetic "none" choice.
    /// </summary>
    public bool IsNone => Id is null;

    /// <summary>
    /// True for a real, persisted list (positive id) - not the "none" sentinel.
    /// </summary>
    public bool IsReal => Id is > 0;
}
