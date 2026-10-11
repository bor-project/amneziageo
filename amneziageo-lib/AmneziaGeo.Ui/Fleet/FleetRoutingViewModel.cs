using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Экран маршрутизации, пока машина держит несколько туннелей: правило списка называет сервер, на котором едет.
/// </summary>
internal sealed class FleetRoutingViewModel : RoutingViewModel
{
    private readonly IAgentConnection _link;
    private IReadOnlyList<string> _servers = [];
    private IReadOnlyDictionary<string, string> _targets = FleetTargets.Unaddressed;

    /// <summary>
    /// ctor
    /// </summary>
    public FleetRoutingViewModel(MainWindowViewModel host, IAgentConnection connection, UiPreferences prefs)
        : base(host, connection, prefs)
    {
        _link = connection;
    }

    /// <inheritdoc/>
    public override void Apply(StatusSnapshot snapshot)
    {
        base.Apply(snapshot);

        // С выключенным режимом набора в снимке нет, и правило снова везёт единственный туннель.
        var fleet = snapshot.Fleet;
        _servers = fleet is null ? [] : [.. fleet.Servers.Select(server => server.Name)];
        _targets = fleet?.Targets ?? FleetTargets.Unaddressed;
        (RoutingEditor as FleetRoutingListEditorViewModel)?.Describe(_servers, _targets);
    }

    /// <inheritdoc/>
    protected override RoutingListEditorViewModel NewEditor(long id, string name, Action<long>? onSaved)
    {
        return Described(new FleetRoutingListEditorViewModel(_link, id, name, onSaved));
    }

    /// <inheritdoc/>
    protected override RoutingListEditorViewModel NewDraft(Action<long>? onSaved)
    {
        return Described(new FleetRoutingListEditorViewModel(_link, onSaved));
    }

    // Редактор с первой строки знает серверы набора и адреса его правил.
    private FleetRoutingListEditorViewModel Described(FleetRoutingListEditorViewModel editor)
    {
        editor.Describe(_servers, _targets);
        return editor;
    }
}
