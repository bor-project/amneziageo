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
    public bool ProxyEnabled => _proxyEntry?.TransportProxy is not null && _proxyEntry.TransportProxyEnabled;
    public bool CanToggleProxy => _proxyEntry?.TransportProxy is { } reference
        && (ProxyEnabled || _proxyCatalog.Any(one => one.Reference == reference && !one.Missing && one.Fault != "proxy-platform-unsupported"
            && one.CompatibleConfigs?.Contains(Name, StringComparer.Ordinal) == true));
    public Func<ConfigItemViewModel, Task<bool>>? SaveProxy { get; set; }
    private CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? _toggleProxy;
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand ToggleProxyCommand => _toggleProxy ??= new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () =>
    {
        if (_proxyEntry is not { TransportProxy: not null } entry || !CanToggleProxy || SaveProxy is null)
        {
            return;
        }

        _proxyEntry = entry with { TransportProxyEnabled = !entry.TransportProxyEnabled };
        RefreshTransportProxyWords();
        if (!await SaveProxy(this))
        {
            _proxyEntry = entry;
            RefreshTransportProxyWords();
        }
    });
    public ProxyReference? SelectedProxyReference => _proxyEntry?.TransportProxy;
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
        OnPropertyChanged(nameof(ProxyEnabled));
        OnPropertyChanged(nameof(CanToggleProxy));
        OnPropertyChanged(nameof(Tags));
    }
}
