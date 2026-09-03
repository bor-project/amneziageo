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

    // Адреса правил, набранные в редакторе и ещё не отданные набору.
    private readonly Dictionary<string, RuleRoute> _held = new(StringComparer.Ordinal);
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
        DropSettled();
        RebuildRuleItems();
    }

    /// <summary>
    /// Куда правило едет по редактору: набранное, а пока его нет - стоящее у набора.
    /// </summary>
    public RuleRoute RouteOf(string token) => _held.TryGetValue(token, out var held) ? held : Stored(token);

    /// <summary>
    /// Держит адрес правила до сохранения.
    /// </summary>
    public void Hold(string token, RuleRoute route)
    {
        if (route == Stored(token))
        {
            _held.Remove(token);
        }
        else
        {
            _held[token] = route;
        }

        RefreshDirty();
    }

    /// <inheritdoc/>
    protected override bool HasPendingEdits => _held.Count > 0;

    /// <inheritdoc/>
    public override async Task<bool> CommitAsync()
    {
        if (!await base.CommitAsync())
        {
            return false;
        }

        foreach (var pair in _held.ToList())
        {
            if (!await AddressAsync(pair.Key, pair.Value.Target.Format(), pair.Value.Fallback.Format()))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override void CaptureBaseline()
    {
        _held.Clear();
        base.CaptureBaseline();
    }

    /// <inheritdoc/>
    public override void Revert()
    {
        _held.Clear();
        base.Revert();
    }

    /// <summary>
    /// Говорит набору, куда едет правило.
    /// </summary>
    public async Task<bool> AddressAsync(string token, string target, string fallback)
    {
        var ack = await _link.SendCommandAsync(new IpcCommand(FleetOps.SetTarget,
            [Id.ToString(CultureInfo.InvariantCulture), token, target, fallback]));
        if (!ack.Ok)
        {
            StatusMessage = FleetNotice.Of(ack);
        }

        return ack.Ok;
    }

    /// <inheritdoc/>
    protected override RoutingRuleItemViewModel NewRuleRow(string token)
    {
        // Сервер называет только правило, ведущее в туннель: прямое и заблокированное читаются одинаково всеми.
        if (RouteChoices.Count == 0 || SelectedRole is "direct" or "block")
        {
            return base.NewRuleRow(token);
        }

        return new FleetRoutingRuleItemViewModel(token, this, RouteOf(token));
    }

    // Адрес правила, стоящий у набора.
    private RuleRoute Stored(string token) =>
        RuleRoute.Parse(_targets.GetValueOrDefault(FleetTargets.Key(Id, token)));

    // Набранное, до чего набор уже дошёл сам, перестаёт быть правкой.
    private void DropSettled()
    {
        foreach (var token in _held.Keys.ToList())
        {
            if (_held[token] == Stored(token))
            {
                _held.Remove(token);
            }
        }

        RefreshDirty();
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
