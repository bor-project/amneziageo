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
        if (RoutingEditor is not FleetRoutingListEditorViewModel editor)
        {
            return;
        }

        // С выключенным режимом набора в снимке нет, и правило снова везёт единственный туннель.
        var fleet = snapshot.Fleet;
        editor.Describe(
            fleet is null ? [] : [.. fleet.Servers.Select(server => server.Name)],
            fleet?.Targets ?? FleetTargets.Unaddressed);
    }

    /// <inheritdoc/>
    protected override RoutingListEditorViewModel NewEditor(long id, string name, Action<long>? onSaved)
    {
        return new FleetRoutingListEditorViewModel(_link, id, name, onSaved);
    }

    /// <inheritdoc/>
    protected override RoutingListEditorViewModel NewDraft(Action<long>? onSaved)
    {
        return new FleetRoutingListEditorViewModel(_link, onSaved);
    }
}
