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
    private bool _selectedProxyEnabled;
    private bool _baseProxyEnabled;
    private bool _savingProxy;
    private Task _proxySave = Task.CompletedTask;
    public event Action<ConfigTransportViewModel>? ProxyListRequested;
    public ProxyDestination? ProxyDestination => _draftProxyTarget ?? (ProxyTargetRules.NormalizeHost(ConfigServices.Host("[Peer]\nEndpoint = " + _endpoint)) is { } host
        ? new ProxyDestination(host, ConfigServices.EndpointPort("[Peer]\nEndpoint = " + _endpoint)) : null);
    public string ProxyTargetIdentity => $"{_draftProxyTarget?.Host}:{_draftProxyTarget?.Port}|{_draftProxyServerKey}|{_endpoint}";
    public string SelectedProxyProvider => SelectedSummary is { } summary ? TransportProxyPresentation.Source(summary.Local, summary.Sources) : string.Empty;
    public string SelectedProxyAddress => SelectedSummary is { } summary ? TransportProxyWords.Address(summary.Host, summary.Port, summary.Path) : Loc.Instance.Get("Proxies_NotSelected");
    public string SelectedProxyProtocol => SelectedSummary is { } summary ? TransportProxyPresentation.Transport(summary.Transport, summary.Http?.Method) : string.Empty;
    public bool SelectedProxyCdn => SelectedSummary?.Cdn == true;
    public bool CanEnableSelectedProxy => HasSelectedProxy && (_selectedProxyEnabled
        || SelectedSummary is { Missing: false } summary && ProxyCompatible(summary));
    public bool SelectedProxyEnabled
    {
        get => HasSelectedProxy && _selectedProxyEnabled;
        set
        {
            if (_choosingProxy || !HasSelectedProxy || value && !CanEnableSelectedProxy || _selectedProxyEnabled == value)
            {
                return;
            }

            _selectedProxyEnabled = value;
            _proxyChosen = true;
            RefreshProxyDetails();
            MarkDirty();
            _ = SaveProxySelectionAsync();
        }
    }

    [RelayCommand]
    private void ChooseProxy() => ProxyListRequested?.Invoke(this);

    [RelayCommand]
    private void ToggleSelectedProxy() => SelectedProxyEnabled = !SelectedProxyEnabled;

    public bool AllowsProxy(TransportProxySummary summary) => ProxyCompatible(summary);

    public void SelectFromPicker(TransportProxySummary summary, bool selected)
    {
        if (selected && !ProxyCompatible(summary) || !selected && SelectedProxyChoice?.Reference != summary.Reference)
        {
            return;
        }

        var enabled = SelectedProxyEnabled;
        _choosingProxy = true;
        SelectedProxyChoice = selected
            ? ProxyChoices.FirstOrDefault(one => one.Reference == summary.Reference) ?? new TransportProxyChoice(summary.Reference, summary.Host)
            : ProxyChoices.FirstOrDefault(one => one.Reference is null) ?? new TransportProxyChoice(null, Loc.Instance.Get("Proxies_None"));
        _selectedProxyEnabled = selected && enabled;
        _proxyChosen = true;
        _choosingProxy = false;
        RefreshProxyDetails();
        MarkDirty();
        _ = SaveProxySelectionAsync();
    }

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

            var enabled = SelectedProxyEnabled;
            if (!SetProperty(ref _selectedProxyChoice, value) || _choosingProxy)
            {
                return;
            }

            _selectedProxyEnabled = value?.Reference is not null && enabled;
            RefreshProxyDetails();
            _proxyChosen = true;
            MarkDirty();
            _ = SaveProxySelectionAsync();
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
        ? Loc.Instance.Get("Proxies_Missing") : SelectedSummary is { Fault: "proxy-platform-unsupported" }
            ? TransportProxyWords.Fault("proxy-platform-unsupported") : SelectedSummary is { } selected && !ProxyCompatible(selected)
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
            nameof(RunningProxyIsHttpGet), nameof(CanViewRunningProxy), nameof(SelectedProxyProvider), nameof(SelectedProxyAddress),
            nameof(SelectedProxyProtocol), nameof(SelectedProxyCdn), nameof(SelectedProxyEnabled), nameof(CanEnableSelectedProxy) })
        {
            OnPropertyChanged(name);
        }
    }

    private bool ProxySelectionDirty => _proxyChosen != _baseProxyChosen || SelectedProxyChoice?.Reference != _baseProxyReference
        || _selectedProxyEnabled != _baseProxyEnabled;

    private bool ProxyConnectionChanged => (_selectedProxyEnabled ? SelectedProxyChoice?.Reference : null) != (_baseProxyEnabled ? _baseProxyReference : null)
        || (!_proxyChosen && UseWebSocket && WebSocketOpen) != (!_baseProxyChosen && _baseUseWebSocket && WebSocketOpen);

    public void ApplyProxyCatalog(IReadOnlyList<TransportProxySummary> summaries, ProxyReference? reference, bool chosen, bool enabled = true)
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
                label += $" · {(summary.Fault == "proxy-platform-unsupported" ? TransportProxyWords.Fault(summary.Fault) : Loc.Instance.Get("Proxies_TargetDenied"))}";
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
            _selectedProxyEnabled = reference is not null && enabled;
            _baseProxyEnabled = _selectedProxyEnabled;
        }

        _choosingProxy = false;
        RefreshProxyDetails();
    }

    public void RefreshProxyCatalog(IReadOnlyList<TransportProxySummary> summaries) =>
        ApplyProxyCatalog(summaries, SelectedProxyChoice?.Reference, _proxyChosen, _selectedProxyEnabled);

    public void RefreshProxyEndpoint(string endpoint)
    {
        if (_endpoint == endpoint)
        {
            return;
        }

        _endpoint = endpoint;
        RefreshProxyCatalog(_proxySummaries);
        OnPropertyChanged(nameof(ProxyTargetIdentity));
        OnPropertyChanged(nameof(WebSocketHostDefault));
        OnPropertyChanged(nameof(WebSocketPortDefault));
    }

    /// <summary>
    /// Refreshes applicability from an imported configuration.
    /// </summary>
    public void SeedProxyTarget(string text)
    {
        RefreshNetworkValues(_resolvedMtu, Ipv6Plan.Available(text));
        var host = ConfigServices.Host(text);
        var port = ConfigServices.EndpointPort(text);
        _draftProxyTarget = ProxyTargetRules.NormalizeHost(host) is { } normalized && port is >= 1 and <= 65535
            ? new ProxyDestination(normalized, port) : null;
        _draftProxyServerKey = ConfigServices.Target(text)?.ServerKey ?? string.Empty;
        ApplyProxyCatalog(_proxySummaries, SelectedProxyChoice?.Reference, _proxyChosen, _selectedProxyEnabled);
        OnPropertyChanged(nameof(ProxyTargetIdentity));
    }

    private bool ProxyCompatible(TransportProxySummary summary)
    {
        if (summary.Local && summary.OwnerConfig != ConfigName)
        {
            return false;
        }
        if (summary.Missing || summary.Fault == "proxy-platform-unsupported")
        {
            return false;
        }

        if (_draftProxyTarget is null && summary.CompatibleConfigs?.Contains(ConfigName, StringComparer.Ordinal) == true)
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
        _baseProxyEnabled = _selectedProxyEnabled;
    }

    private void RevertProxyChoice()
    {
        if (_savingProxy)
        {
            return;
        }

        _choosingProxy = true;
        var choice = ProxyChoices.FirstOrDefault(one => one.Reference == _baseProxyReference);
        if (choice is null)
        {
            choice = new TransportProxyChoice(_baseProxyReference, Loc.Instance.Get(_baseProxyReference is null ? "Proxies_None" : "Proxies_Missing"), _baseProxyReference is null);
            ProxyChoices.Add(choice);
        }

        SelectedProxyChoice = choice;
        _proxyChosen = _baseProxyChosen;
        _selectedProxyEnabled = _baseProxyEnabled;
        _choosingProxy = false;
        RefreshProxyDetails();
    }

    public Task SaveProxySelectionAsync()
    {
        if (!_applying && ConfigName.Length > 0 && !_savingProxy)
        {
            _proxySave = WriteProxySelectionAsync();
        }

        return _proxySave;
    }

    private async Task WriteProxySelectionAsync()
    {
        _savingProxy = true;
        try
        {
            while (ProxySelectionDirty)
            {
                var reference = SelectedProxyChoice?.Reference;
                var enabled = SelectedProxyEnabled;
                var chosen = _proxyChosen;
                var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectTransportProxy,
                    [ConfigName, JsonSerializer.Serialize(reference, IpcJson.Options), enabled ? "on" : "off"]));
                if (!ack.Ok)
                {
                    StatusMessage = TransportProxyWords.Fault(ack.Message);
                    return;
                }

                _baseProxyReference = reference;
                _baseProxyEnabled = enabled;
                _baseProxyChosen = chosen;
                MarkDirty();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("{0}", ex);
            StatusMessage = Loc.Instance.Get("Proxies_RequestFailed");
        }
        finally
        {
            _savingProxy = false;
        }
    }
}
