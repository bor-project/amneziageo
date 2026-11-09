using System.Collections.ObjectModel;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

internal sealed partial class ConfigTransportViewModel
{
    private TransportProxyChoice? _selectedProxyChoice;
    private ProxyReference? _baseProxyReference;
    private bool _proxyChosen;
    private bool _baseProxyChosen;
    private bool _choosingProxy;
    private readonly Dictionary<ProxyReference, IReadOnlyList<string>> _proxySources = [];
    private IReadOnlyList<TransportProxySummary> _proxySummaries = [];
    private ProxyDestination? _draftProxyTarget;
    private string _draftProxyServerKey = string.Empty;

    public ObservableCollection<TransportProxyChoice> ProxyChoices { get; } = [];

    public TransportProxyChoice? SelectedProxyChoice
    {
        get => _selectedProxyChoice;
        set
        {
            if (!_choosingProxy && value?.IsSelectable == false)
            {
                return;
            }

            if (!SetProperty(ref _selectedProxyChoice, value) || _choosingProxy || value is null)
            {
                return;
            }

            RefreshProxyDetails();
            _proxyChosen = true;
            MarkDirty();
            FireAutoSave();
        }
    }

    private RunningTransportProxy? _runningProxy;
    private bool _proxyIsRunning;
    public event Action<ProxyReference>? OpenProxyRequested;
    public event Action<bool>? ProxyReturnRequested;
    private bool _viewedRunningProxy;
    public void ReturnFromProxy() => ProxyReturnRequested?.Invoke(_viewedRunningProxy);
    public bool HasSelectedProxy => SelectedProxyChoice?.Reference is not null;
    public string SelectedProxyDetails => TransportProxyPresentation.Selected(_proxySummaries, SelectedProxyChoice?.Reference);
    public string SelectedProxyScope => SelectedSummary is { } summary ? TransportProxyPresentation.Scope(summary) : string.Empty;
    public string SelectedProxyTargets => SelectedSummary is { } summary ? TransportProxyPresentation.ScopeTargets(summary) : string.Empty;
    public string SelectedProxyAvailability => SelectedSummary is { } summary ? TransportProxyWords.Availability(summary.Availability, summary.Missing) : string.Empty;
    public bool SelectedProxyIsHttpGet => SelectedSummary is { Transport: "http", Http: { Version: 2, Method: "GET" } };
    public bool SelectedProxyAvailable => SelectedSummary is { Missing: false, Availability: TransportProxyAvailability.Available };
    public string SelectedProxyFault => HasSelectedProxy && (SelectedSummary is not { } summary || summary.Missing)
        ? Loc.Instance.Get("Proxies_Missing") : SelectedSummary is { } selected && !ProxyCompatible(selected)
            ? Loc.Instance.Get("Proxies_TargetDenied") : string.Empty;
    public bool CanViewSelectedProxy => SelectedSummary is { Missing: false };
    public bool ShowRunningProxy => _proxyIsRunning;
    public bool RunningProxyIsHttpGet => _runningProxy?.Front is { Transport: "http", Http: { Version: 2, Method: "GET" } };
    public string RunningProxyDetails => TransportProxyPresentation.Running(_proxySummaries, _runningProxy);
    public bool CanViewRunningProxy => _proxyIsRunning && TransportProxyPresentation.FindRunning(_proxySummaries, _runningProxy?.Reference) is { Missing: false };
    private TransportProxySummary? SelectedSummary => TransportProxyPresentation.FindExact(_proxySummaries, SelectedProxyChoice?.Reference);

    public void ApplyRunningProxy(RunningTransportProxy? running, bool isRunning)
    {
        _runningProxy = running;
        _proxyIsRunning = isRunning;
        RefreshProxyDetails();
    }

    [RelayCommand]
    private void ViewSelectedProxy()
    {
        if (SelectedSummary is { Missing: false } summary)
        {
            _viewedRunningProxy = false;
            OpenProxyRequested?.Invoke(summary.Reference);
        }
    }

    [RelayCommand]
    private void ViewRunningProxy()
    {
        if (TransportProxyPresentation.FindRunning(_proxySummaries, _runningProxy?.Reference) is { Missing: false } summary)
        {
            _viewedRunningProxy = true;
            OpenProxyRequested?.Invoke(summary.Reference);
        }
    }

    private void RefreshProxyDetails()
    {
        foreach (var name in new[] { nameof(HasSelectedProxy), nameof(SelectedProxyDetails), nameof(SelectedProxyScope),
            nameof(SelectedProxyTargets), nameof(SelectedProxyAvailability), nameof(SelectedProxyAvailable), nameof(SelectedProxyFault),
            nameof(CanViewSelectedProxy), nameof(SelectedProxyIsHttpGet), nameof(ShowRunningProxy), nameof(RunningProxyDetails),
            nameof(RunningProxyIsHttpGet), nameof(CanViewRunningProxy) })
        {
            OnPropertyChanged(name);
        }
    }

    private bool ProxySelectionDirty => _proxyChosen != _baseProxyChosen || SelectedProxyChoice?.Reference != _baseProxyReference;

    public void ApplyProxyCatalog(IReadOnlyList<TransportProxySummary> summaries, ProxyReference? reference, bool chosen)
    {
        _proxySummaries = summaries;
        var keepEdit = ProxySelectionDirty;
        var selected = keepEdit ? SelectedProxyChoice?.Reference : reference;
        var wasChosen = keepEdit ? _proxyChosen : chosen;
        var choices = new List<TransportProxyChoice> { new(null, Loc.Instance.Get("Proxies_None")) };
        foreach (var summary in summaries.Where(one => !one.Missing || one.Reference == selected))
        {
            var compatible = ProxyCompatible(summary);
            if (!compatible && summary.Reference != selected)
            {
                continue;
            }

            if (summary.Sources.Count > 0)
            {
                _proxySources[summary.Reference] = summary.Sources;
            }

            var origins = summary.Sources.Count > 0 ? summary.Sources
                : _proxySources.GetValueOrDefault(summary.Reference) ?? [];
            var source = summary.Local ? Loc.Instance.Get("Proxies_Local")
                : origins.Count > 0 ? string.Join(", ", origins) : Loc.Instance.Get("Proxies_Server");
            var transport = TransportProxyPresentation.Transport(summary.Transport, summary.Http?.Method);
            var label = $"{summary.Name} · {transport} · {source} · {TransportProxyWords.Address(summary.Host, summary.Port, summary.Path)}";
            if (summary.Missing)
            {
                label += $" · {Loc.Instance.Get("Proxies_Missing")}";
            }
            else if (!compatible)
            {
                label += $" · {Loc.Instance.Get("Proxies_TargetDenied")}";
            }

            choices.Add(new TransportProxyChoice(summary.Reference, label, compatible, summary.Transport == "http" && summary.Http is { Version: 2, Method: "GET" }));
        }

        if (selected is not null && !choices.Any(one => one.Reference == selected))
        {
            choices.Add(new TransportProxyChoice(selected, $"{Loc.Instance.Get("Proxies_Missing")} · {selected.Id} · {selected.Source}", false));
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
            if (current != choice)
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
        RefreshProxyDetails();
    }

    /// <summary>
    /// Refreshes applicability from an imported configuration.
    /// </summary>
    public void SeedProxyTarget(string text)
    {
        var host = ConfigServices.Host(text);
        var port = ConfigServices.EndpointPort(text);
        _draftProxyTarget = ProxyTargetRules.NormalizeHost(host) is { } normalized && port is >= 1 and <= 65535
            ? new ProxyDestination(normalized, port) : null;
        _draftProxyServerKey = ConfigServices.Target(text)?.ServerKey ?? string.Empty;
        ApplyProxyCatalog(_proxySummaries, SelectedProxyChoice?.Reference, _proxyChosen);
    }

    private bool ProxyCompatible(TransportProxySummary summary)
    {
        if (summary.Missing)
        {
            return false;
        }

        if (summary.CompatibleConfigs?.Contains(ConfigName, StringComparer.Ordinal) == true)
        {
            return true;
        }

        if (summary.Legacy)
        {
            return _draftProxyTarget is not null && _draftProxyServerKey.Length > 0
                && summary.LegacyIssuer == _draftProxyServerKey && summary.LegacyPort == _draftProxyTarget.Port;
        }

        if (_draftProxyTarget is { } target)
        {
            return ProxyTargetRules.Allows(summary.Targets, target.Host, target.Port);
        }

        if (summary.CompatibleConfigs is not null && summary.Targets?.Restricted == true)
        {
            return false;
        }

        var endpoint = "[Peer]\nEndpoint = " + _endpoint;
        return summary.Targets?.Restricted != true || ProxyTargetRules.Allows(summary.Targets,
            ConfigServices.Host(endpoint), ConfigServices.EndpointPort(endpoint));
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
            choice = new TransportProxyChoice(_baseProxyReference, Loc.Instance.Get(_baseProxyReference is null ? "Proxies_None" : "Proxies_Missing"), _baseProxyReference is null);
            ProxyChoices.Add(choice);
        }

        SelectedProxyChoice = choice;
        _proxyChosen = _baseProxyChosen;
        _choosingProxy = false;
        RefreshProxyDetails();
    }

    private Task<IpcAck> CommitProxyChoiceAsync() => _connection.SendCommandAsync(new IpcCommand(
        IpcContract.OpSelectTransportProxy, [ConfigName, JsonSerializer.Serialize(SelectedProxyChoice?.Reference, IpcJson.Options)]));
}
