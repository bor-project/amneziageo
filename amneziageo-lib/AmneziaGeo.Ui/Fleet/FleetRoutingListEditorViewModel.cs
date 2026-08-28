using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Редактор списка маршрутизации в режиме: каждое правило туннеля называет сервер, на котором едет.
/// </summary>
internal sealed class FleetRoutingListEditorViewModel : RoutingListEditorViewModel
{
    private readonly IAgentConnection _link;
    private IReadOnlyDictionary<string, string> _targets = FleetTargets.Unaddressed;
    private string _shown = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public FleetRoutingListEditorViewModel(IAgentConnection connection, Action<long>? onSaved)
        : base(connection, onSaved)
    {
        _link = connection;
    }

    /// <summary>
    /// ctor
    /// </summary>
    public FleetRoutingListEditorViewModel(IAgentConnection connection, long id, string name, Action<long>? onSaved)
        : base(connection, id, name, onSaved)
    {
        _link = connection;
    }

    /// <summary>
    /// Куда правило может ехать.
    /// </summary>
    public IReadOnlyList<RuleTargetChoice> RouteChoices { get; private set; } = [];

    /// <summary>
    /// Куда оно может уходить, пока названный сервер не поднят.
    /// </summary>
    public IReadOnlyList<RuleTargetChoice> FallbackChoices { get; private set; } = [];

    /// <summary>
    /// Серверы набора и адреса его правил. Пустой список серверов возвращает строки мастера.
    /// </summary>
    public void Describe(IReadOnlyList<string> servers, IReadOnlyDictionary<string, string> targets)
    {
        var stamp = string.Join('\n', servers) + "\u0000"
            + string.Join('\n', targets.OrderBy(target => target.Key, StringComparer.Ordinal).Select(target => $"{target.Key}={target.Value}"));
        if (string.Equals(stamp, _shown, StringComparison.Ordinal))
        {
            return;
        }

        _shown = stamp;
        _targets = targets;
        RouteChoices = Choices(servers, false);
        FallbackChoices = Choices(servers, true);
        RebuildRuleItems();
    }

    /// <summary>
    /// Говорит набору, куда едет правило.
    /// </summary>
    public async Task AddressAsync(string token, string target, string fallback)
    {
        var ack = await _link.SendCommandAsync(new IpcCommand(FleetOps.SetTarget,
            [Id.ToString(CultureInfo.InvariantCulture), token, target, fallback]));
        if (!ack.Ok)
        {
            StatusMessage = FleetNotice.Of(ack);
        }
    }

    /// <inheritdoc/>
    protected override RoutingRuleItemViewModel NewRuleRow(string token)
    {
        // Сервер называет только правило, ведущее в туннель: прямое и заблокированное читаются одинаково всеми.
        if (RouteChoices.Count == 0 || SelectedRole is "direct" or "block")
        {
            return base.NewRuleRow(token);
        }

        return new FleetRoutingRuleItemViewModel(token, this, RuleRoute.Parse(_targets.GetValueOrDefault(FleetTargets.Key(Id, token))));
    }

    // Авто, лучший, каждый сервер библиотеки, а директ и блок - только у второго списка.
    private static IReadOnlyList<RuleTargetChoice> Choices(IReadOnlyList<string> servers, bool fallback)
    {
        if (servers.Count == 0)
        {
            return [];
        }

        var choices = new List<RuleTargetChoice>
        {
            new(RuleTarget.Auto, Loc.Instance.Get("Main_RuleTargetAuto")),
            new(RuleTarget.Best, Loc.Instance.Get("Main_RuleTargetBest")),
        };
        foreach (var name in servers)
        {
            choices.Add(new RuleTargetChoice(name, name));
        }

        if (fallback)
        {
            choices.Add(new RuleTargetChoice(RuleTarget.Direct, Loc.Instance.Get("Main_RuleTargetDirect")));
            choices.Add(new RuleTargetChoice(RuleTarget.Block, Loc.Instance.Get("Main_RuleTargetBlock")));
        }

        return choices;
    }
}
