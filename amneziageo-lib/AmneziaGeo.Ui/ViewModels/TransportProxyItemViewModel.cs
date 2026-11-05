using AmneziaGeo.Decl;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.ViewModels;

internal sealed class TransportProxyItemViewModel : ViewModelBase
{
    public TransportProxySummary Summary { get; private set; }
    public ProxyReference Reference => Summary.Reference;
    public string Name => Summary.Name;
    public string Address => TransportProxyWords.Address(Summary.Host, Summary.Port, Summary.Path);
    public string Transport => Summary.Transport == "xhttp" ? "XHTTP" : "WebSocket";
    public string Source => Summary.Local ? Loc.Instance.Get("Proxies_Local")
        : Summary.Sources.Count > 0 ? string.Join(", ", Summary.Sources) : Loc.Instance.Get("Proxies_Server");
    public string Availability => TransportProxyWords.Availability(Summary.Availability, Summary.Missing);
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
        Summary = summary.Missing && summary.Sources.Count == 0 && Summary.Sources.Count > 0
            ? summary with { Sources = Summary.Sources } : summary;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Transport));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(Unavailable));
    }
}

internal sealed record TransportProxyChoice(ProxyReference? Reference, string Label)
{
    public override string ToString() => Label;
}
