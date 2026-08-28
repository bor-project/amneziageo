using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Правило в режиме: куда оно едет и куда уходит, пока названный сервер не поднят.
/// </summary>
internal sealed partial class FleetRoutingRuleItemViewModel : RoutingRuleItemViewModel
{
    private readonly FleetRoutingListEditorViewModel _editor;
    private readonly bool _seeded;

    /// <summary>
    /// ctor
    /// </summary>
    public FleetRoutingRuleItemViewModel(string token, FleetRoutingListEditorViewModel editor, RuleRoute route)
        : base(token)
    {
        _editor = editor;
        Route = Pick(editor.RouteChoices, route.Target);
        Fallback = Pick(editor.FallbackChoices, route.Fallback);
        _seeded = true;
    }

    /// <inheritdoc/>
    public override bool CanExpand => true;

    /// <inheritdoc/>
    public override bool HasRouteStrip => true;

    /// <inheritdoc/>
    public override string ExpandTooltip =>
        Loc.Instance.Get(CanPreview ? "Main_RuleRouteEntriesTooltip" : "Main_RuleRouteTooltip");

    /// <summary>
    /// Куда правило едет.
    /// </summary>
    public IReadOnlyList<RuleTargetChoice> RouteChoices => _editor.RouteChoices;

    /// <summary>
    /// Куда оно уходит, пока названный сервер не поднят.
    /// </summary>
    public IReadOnlyList<RuleTargetChoice> FallbackChoices => _editor.FallbackChoices;

    /// <summary>
    /// Подпись списка маршрута.
    /// </summary>
    public string RouteLabel => Loc.Instance.Get("Main_RuleRouteLabel");

    /// <inheritdoc cref="RouteLabel"/>
    public string FallbackLabel => Loc.Instance.Get("Main_RuleFallbackLabel");

    [ObservableProperty]
    private RuleTargetChoice? _route;

    [ObservableProperty]
    private RuleTargetChoice? _fallback;

    partial void OnRouteChanged(RuleTargetChoice? value)
    {
        Address();
    }

    partial void OnFallbackChanged(RuleTargetChoice? value)
    {
        Address();
    }

    private void Address()
    {
        if (!_seeded)
        {
            return;
        }

        _ = _editor.AddressAsync(Token, Route?.Word ?? RuleTarget.Auto, Fallback?.Word ?? RuleTarget.Auto);
    }

    // Строка списка, которой отвечает хранимое слово; сервер, которого больше нет, читается как «авто».
    private static RuleTargetChoice? Pick(IReadOnlyList<RuleTargetChoice> choices, RuleTarget target)
    {
        var word = target.Format();
        foreach (var choice in choices)
        {
            if (string.Equals(choice.Word, word, StringComparison.Ordinal))
            {
                return choice;
            }
        }

        return choices.Count > 0 ? choices[0] : null;
    }
}
