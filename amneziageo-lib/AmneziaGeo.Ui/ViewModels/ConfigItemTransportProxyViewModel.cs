using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.ViewModels;

internal partial class ConfigItemViewModel
{
    private ConfigEntry? _proxyEntry;
    private IReadOnlyList<TransportProxySummary> _proxyCatalog = [];
    public string SelectedProxyText => $"{Loc.Instance.Get("Proxies_Selected")}: {TransportProxyPresentation.Selected(_proxyCatalog, _proxyEntry?.TransportProxy)}";
    public bool HasProxySelection => _proxyEntry is { TransportProxyChosen: true } or { TransportProxy: not null };
    public bool HasRunningProxy => _proxyEntry?.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;
    public string ActualProxyText => $"{Loc.Instance.Get("Proxies_ActuallyUsed")}: {TransportProxyPresentation.Running(_proxyCatalog, _proxyEntry?.RunningTransport)}";

    public void ApplyTransportProxy(ConfigEntry entry, IReadOnlyList<TransportProxySummary> catalog)
    {
        _proxyEntry = entry;
        _proxyCatalog = catalog;
        RefreshTransportProxyWords();
    }

    public void RefreshTransportProxyWords()
    {
        OnPropertyChanged(nameof(SelectedProxyText));
        OnPropertyChanged(nameof(HasProxySelection));
        OnPropertyChanged(nameof(HasRunningProxy));
        OnPropertyChanged(nameof(ActualProxyText));
    }
}
