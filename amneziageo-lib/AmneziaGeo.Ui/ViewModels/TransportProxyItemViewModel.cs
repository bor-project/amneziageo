using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.Controls;

namespace AmneziaGeo.Ui.ViewModels;

internal sealed class TransportProxyItemViewModel : ViewModelBase
{
    public TransportProxySummary Summary { get; private set; }
    public ProxyReference Reference => Summary.Reference;
    public string Name => Summary.Name;
    public string Address => TransportProxyWords.Address(Summary.Host, Summary.Port, Summary.Path);
    public string Transport => TransportProxyPresentation.Transport(Summary.Transport, Summary.Http?.Method);
    public bool IsHttpGet => Summary.Transport == "http" && Summary.Http is { Version: 2, Method: "GET" };
    public string Source => Summary.Local ? Loc.Instance.Get("Proxies_Local")
        : Summary.Sources.Count > 0 ? string.Join(", ", Summary.Sources) : Loc.Instance.Get("Proxies_Server");
    public string Availability => TransportProxyWords.Availability(Summary.Availability, Summary.Missing);
    public string Fault => Summary.Fault.Length > 0 && !Summary.Missing ? TransportProxyWords.Fault(Summary.Fault) : string.Empty;
    public string Scope => TransportProxyPresentation.Scope(Summary);
    public string ScopeTargets => TransportProxyPresentation.ScopeTargets(Summary);
    public string ActionText => Loc.Instance.Get(Summary.Local && !Summary.Missing ? "Proxies_Settings" : "Proxies_View");
    private IReadOnlyList<ConfigEntry> _configs = [];
    private IReadOnlyList<TransportProxySummary> _catalog = [];
    public string ChosenBy => Names(false) is { Length: > 0 } names ? Loc.Instance.Get("Proxies_ChosenBy", names) : string.Empty;
    public string UsedBy => Names(true) is { Length: > 0 } names ? Loc.Instance.Get("Proxies_UsedBy", names) : string.Empty;
    public bool HasChosen => ChosenBy.Length > 0;
    public bool HasUsed => UsedBy.Length > 0;
    public void ApplyUsage(IReadOnlyList<ConfigEntry> configs, IReadOnlyList<TransportProxySummary>? catalog = null)
    {
        _configs = configs;
        _catalog = catalog ?? [Summary];
        Refresh();
    }

    private string Names(bool running) => string.Join(", ", _configs.Where(one => running
        ? one.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting
            && one.RunningTransport is { } actual && TransportProxyPresentation.FindRunning(_catalog, actual.Reference)?.Reference == Reference
        : one.TransportProxy == Reference).Select(one => one.Name));

    public bool Available => !Summary.Missing && Summary.Availability == TransportProxyAvailability.Available;
    public bool Unavailable => Summary.Missing || Summary.Availability == TransportProxyAvailability.Unavailable;

    /// <summary>
    /// ctor
    /// </summary>
    public TransportProxyItemViewModel(TransportProxySummary summary)
    {
        Summary = summary;
    }

    public void Apply(TransportProxySummary summary)
    {
        if (_catalog.Count == 1 && _catalog[0].Reference == Reference)
        {
            _catalog = [summary];
        }

        Summary = summary.Missing && summary.Sources.Count == 0 && Summary.Sources.Count > 0
            ? summary with { Sources = Summary.Sources } : summary;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Transport));
        OnPropertyChanged(nameof(IsHttpGet));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(Fault));
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(Unavailable));
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(ScopeTargets));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ChosenBy));
        OnPropertyChanged(nameof(UsedBy));
        OnPropertyChanged(nameof(HasChosen));
        OnPropertyChanged(nameof(HasUsed));
    }
}

internal sealed record TransportProxyChoice(ProxyReference? Reference, string Label, bool IsSelectable = true, bool IsHttpGet = false) : IAdaptiveSelectOption
{
    public override string ToString() => Label;
}
