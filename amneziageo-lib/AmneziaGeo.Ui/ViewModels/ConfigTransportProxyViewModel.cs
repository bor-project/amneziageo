using System.Collections.ObjectModel;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.ViewModels;

internal sealed partial class ConfigTransportViewModel
{
    private TransportProxyChoice? _selectedProxyChoice;
    private ProxyReference? _baseProxyReference;
    private bool _proxyChosen;
    private bool _baseProxyChosen;
    private bool _choosingProxy;
    private readonly Dictionary<ProxyReference, IReadOnlyList<string>> _proxySources = [];

    public ObservableCollection<TransportProxyChoice> ProxyChoices { get; } = [];

    public TransportProxyChoice? SelectedProxyChoice
    {
        get => _selectedProxyChoice;
        set
        {
            if (!SetProperty(ref _selectedProxyChoice, value) || _choosingProxy || value is null)
            {
                return;
            }

            _proxyChosen = true;
            MarkDirty();
            FireAutoSave();
        }
    }

    private bool ProxySelectionDirty => _proxyChosen != _baseProxyChosen || SelectedProxyChoice?.Reference != _baseProxyReference;

    public void ApplyProxyCatalog(IReadOnlyList<TransportProxySummary> summaries, ProxyReference? reference, bool chosen)
    {
        var keepEdit = ProxySelectionDirty;
        var selected = keepEdit ? SelectedProxyChoice?.Reference : reference;
        var wasChosen = keepEdit ? _proxyChosen : chosen;
        var choices = new List<TransportProxyChoice> { new(null, Loc.Instance.Get("Proxies_None")) };
        foreach (var summary in summaries.Where(one => !one.Missing || one.Reference == selected))
        {
            if (summary.Sources.Count > 0)
            {
                _proxySources[summary.Reference] = summary.Sources;
            }

            var origins = summary.Sources.Count > 0 ? summary.Sources
                : _proxySources.GetValueOrDefault(summary.Reference) ?? [];
            var source = summary.Local ? Loc.Instance.Get("Proxies_Local")
                : origins.Count > 0 ? string.Join(", ", origins) : Loc.Instance.Get("Proxies_Server");
            var transport = summary.Transport == "xhttp" ? "XHTTP" : "WebSocket";
            var label = $"{summary.Name} · {transport} · {source} · {TransportProxyWords.Address(summary.Host, summary.Port, summary.Path)}";
            if (summary.Missing)
            {
                label += $" · {Loc.Instance.Get("Proxies_Missing")}";
            }

            choices.Add(new TransportProxyChoice(summary.Reference, label));
        }

        if (selected is not null && !choices.Any(one => one.Reference == selected))
        {
            choices.Add(new TransportProxyChoice(selected, $"{Loc.Instance.Get("Proxies_Missing")} · {selected.Id}"));
        }

        _choosingProxy = true;
        for (var at = ProxyChoices.Count - 1; at >= 0; at--)
        {
            if (!choices.Any(one => one.Reference == ProxyChoices[at].Reference))
            {
                ProxyChoices.RemoveAt(at);
            }
        }

        for (var at = 0; at < choices.Count; at++)
        {
            var choice = choices[at];
            var current = ProxyChoices.FirstOrDefault(one => one.Reference == choice.Reference);
            if (current is null)
            {
                ProxyChoices.Insert(at, choice);
                continue;
            }

            var old = ProxyChoices.IndexOf(current);
            if (current.Label != choice.Label)
            {
                ProxyChoices[old] = choice;
            }

            if (old != at)
            {
                ProxyChoices.Move(old, at);
            }
        }

        SelectedProxyChoice = ProxyChoices.First(one => one.Reference == selected);
        _proxyChosen = wasChosen;
        if (!keepEdit)
        {
            _baseProxyReference = reference;
            _baseProxyChosen = chosen;
        }

        _choosingProxy = false;
    }

    private void CaptureProxyBaseline()
    {
        _baseProxyReference = SelectedProxyChoice?.Reference;
        _baseProxyChosen = _proxyChosen;
    }

    private void RevertProxyChoice()
    {
        _choosingProxy = true;
        var choice = ProxyChoices.FirstOrDefault(one => one.Reference == _baseProxyReference);
        if (choice is null)
        {
            choice = new TransportProxyChoice(_baseProxyReference, Loc.Instance.Get(_baseProxyReference is null ? "Proxies_None" : "Proxies_Missing"));
            ProxyChoices.Add(choice);
        }

        SelectedProxyChoice = choice;
        _proxyChosen = _baseProxyChosen;
        _choosingProxy = false;
    }

    private Task<IpcAck> CommitProxyChoiceAsync() => _connection.SendCommandAsync(new IpcCommand(
        IpcContract.OpSelectTransportProxy, [ConfigName, JsonSerializer.Serialize(SelectedProxyChoice?.Reference, IpcJson.Options)]));
}
