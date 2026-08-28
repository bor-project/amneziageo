using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Правило по именам: сервера у него нет, и открытая строка говорит почему.
/// </summary>
internal sealed class FleetNamedRuleItemViewModel(string token) : RoutingRuleItemViewModel(token)
{
    /// <inheritdoc/>
    public override bool CanExpand => true;

    /// <inheritdoc/>
    public override bool HasRouteStrip => true;

    /// <inheritdoc/>
    public override string ExpandTooltip => Loc.Instance.Get(CanPreview ? "Main_RuleEntriesTooltip" : "Main_RuleByName");

    /// <summary>
    /// Почему правилу не назначить сервер.
    /// </summary>
    public string RouteNote => Loc.Instance.Get("Main_RuleByName");
}
