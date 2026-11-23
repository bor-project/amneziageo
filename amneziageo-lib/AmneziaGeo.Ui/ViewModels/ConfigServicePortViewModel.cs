using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

internal sealed partial class ConfigTransportViewModel
{
    private string _baseServicesPort = string.Empty;
    private int _defaultServicesPort;

    [ObservableProperty]
    private string _servicesPort = string.Empty;

    public string ServicesPortDefault => _defaultServicesPort > 0
        ? _defaultServicesPort.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private bool ServicePortDirty => !string.Equals(ServicesPort, _baseServicesPort, StringComparison.Ordinal);

    partial void OnServicesPortChanged(string value) => MarkDirty();

    public void ApplyServicesPort(int port, int fallback)
    {
        _defaultServicesPort = fallback;
        OnPropertyChanged(nameof(ServicesPortDefault));
        if (!ServicePortDirty)
        {
            _baseServicesPort = port > 0 ? port.ToString(CultureInfo.InvariantCulture) : string.Empty;
            ServicesPort = _baseServicesPort;
        }
    }
}
