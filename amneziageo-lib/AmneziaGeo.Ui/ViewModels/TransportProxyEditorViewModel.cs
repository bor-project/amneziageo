using System.Globalization;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

internal sealed partial class TransportProxyEditorViewModel : ViewModelBase, IEditScope
{
    private readonly IAgentConnection _connection;
    private readonly bool _legacy;
    private readonly OfferedProxy? _original;
    private OfferedProxy? _baseline;
    private bool _applying;
    private readonly bool _local;
    private string _source;
    private TransportProxyAvailability _availability;
    private bool _missing;

    public ProxyReference? Reference { get; private set; }
    public bool IsNew => Reference is null;
    public bool Editable { get; }
    public bool ModernEditable => Editable && !_legacy;
    public bool ShowCredential => Editable;
    public bool ShowTls => Editable;
    public bool ShowHttpMethod => TransportIndex == 1;
    public bool CertificateEditable => false;
    public bool ShowTargets => !_legacy;
    public string Source => _local ? Loc.Instance.Get("Proxies_Local")
        : _source.Length > 0 ? _source : Loc.Instance.Get("Proxies_Server");
    public string Availability => TransportProxyWords.Availability(_availability, _missing);
    public string[] Transports { get; private set; } = ["WebSocket", "HTTP", "QUIC"];
    public string[] Methods { get; } = ["GET", "POST"];
    public string CredentialLabel => Loc.Instance.Get(_legacy ? "Proxies_BasicCredential" : "Proxies_BearerCredential");
    public string Title => IsNew ? Loc.Instance.Get("Proxies_New") : Name;

    [ObservableProperty]
    private string _name = string.Empty;
    [ObservableProperty]
    private int _transportIndex;
    [ObservableProperty]
    private int _methodIndex;
    [ObservableProperty]
    private bool _useCertificate = true;
    [ObservableProperty]
    private string _host = string.Empty;
    [ObservableProperty]
    private string _port = "443";
    [ObservableProperty]
    private string _path = string.Empty;
    [ObservableProperty]
    private string _serverName = string.Empty;
    [ObservableProperty]
    private string _pin = string.Empty;
    [ObservableProperty]
    private string _credential = string.Empty;
    [ObservableProperty]
    private bool _restricted;
    [ObservableProperty]
    private string _targetsText = string.Empty;
    [ObservableProperty]
    private string _statusMessage = string.Empty;
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public TransportProxyEditorViewModel(IAgentConnection connection, CatalogTransportProxy? local = null, TransportProxySummary? summary = null)
    {
        _connection = connection;
        if (local is not null)
        {
            local = local with { Proxy = ProxyTransportModes.Normalize(local.Proxy) };
        }
        _legacy = local?.Legacy == true || summary?.Legacy == true;
        _original = local?.Proxy;
        Reference = local?.Reference ?? summary?.Reference;
        Editable = local is not null || summary is null;
        _local = summary is null || summary.Local;
        _source = summary is null ? string.Empty : string.Join(", ", summary.Sources);
        _availability = summary?.Availability ?? TransportProxyAvailability.Unknown;
        _missing = summary?.Missing == true;
        _name = local?.Proxy.Name ?? summary?.Name ?? string.Empty;
        _transportIndex = Index(local?.Proxy.Transport ?? summary?.Transport);
        if (_transportIndex == 3)
        {
            Transports = ["WebSocket", "HTTP", "QUIC", "XHTTP"];
        }
        _methodIndex = (local?.Proxy.Http?.Method ?? summary?.Http?.Method) == "POST" ? 1 : 0;
        _useCertificate = true;
        _host = local?.Proxy.Host ?? summary?.Host ?? string.Empty;
        _port = (local?.Proxy.Port ?? summary?.Port ?? 443).ToString(CultureInfo.InvariantCulture);
        _path = local?.Proxy.Path ?? summary?.Path ?? string.Empty;
        _serverName = local?.Proxy.Tls.ServerName ?? string.Empty;
        _pin = local?.Proxy.Tls.Pin ?? string.Empty;
        _credential = local?.Proxy.Auth.Credential ?? string.Empty;
        var targets = local?.Proxy.Targets ?? summary?.Targets;
        _restricted = targets?.Restricted == true;
        _targetsText = string.Join(Environment.NewLine, targets?.Destinations.Select(ProxyTargetRules.Format) ?? []);
        CaptureBaseline();
    }

    public bool IsDirty { get; private set; }
    public bool ConnectionChanged => Editable && !IsNew && _baseline is not null
        && !TransportProxyChanges.SameDefinition(Draft(), _baseline);
    public event EventHandler? DirtyChanged;

    /// <summary>
    /// Compares permission for one UDP destination.
    /// </summary>
    public bool TargetPermissionChanged(string host, int port) => _baseline is not null
        && ProxyTargetRules.Allows(_baseline.Targets, host, port) != ProxyTargetRules.Allows(Draft().Targets, host, port);

    partial void OnNameChanged(string value) { Changed(); OnPropertyChanged(nameof(Title)); }
    partial void OnTransportIndexChanged(int value)
    {
        if (value == 3 && Transports.Length == 3)
        {
            Transports = ["WebSocket", "HTTP", "QUIC", "XHTTP"];
            OnPropertyChanged(nameof(Transports));
        }

        if (value == 2)
        {
            UseCertificate = true;
        }

        Changed();
        OnPropertyChanged(nameof(CredentialLabel));
        OnPropertyChanged(nameof(ShowHttpMethod));
        OnPropertyChanged(nameof(CertificateEditable));
    }
    partial void OnMethodIndexChanged(int value) => Changed();
    partial void OnUseCertificateChanged(bool value)
    {
        if (!value)
        {
            UseCertificate = true;
            return;
        }

        Changed();
        OnPropertyChanged(nameof(ShowTls));
    }
    partial void OnHostChanged(string value) => Changed();
    partial void OnPortChanged(string value) => Changed();
    partial void OnPathChanged(string value) => Changed();
    partial void OnServerNameChanged(string value) => Changed();
    partial void OnPinChanged(string value) => Changed();
    partial void OnCredentialChanged(string value) => Changed();
    partial void OnRestrictedChanged(bool value) => Changed();
    partial void OnTargetsTextChanged(string value) => Changed();

    private void Changed()
    {
        if (_applying)
        {
            return;
        }

        StatusMessage = string.Empty;
        var draft = Draft();
        IsDirty = Editable && (IsNew || _baseline is null || (draft with { Targets = null }) != (_baseline with { Targets = null })
            || !ProxyTargetRules.Same(draft.Targets, _baseline.Targets));
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int Index(string? transport) => transport switch
    {
        "http" => 1, "quic" => 2, "xhttp" => 3, _ => 0,
    };

    private OfferedProxy Draft()
    {
        var transport = TransportIndex switch { 1 => "http", 2 => "quic", 3 => "xhttp", _ => "websocket" };
        var method = MethodIndex == 1 ? "POST" : "GET";
        return new OfferedProxy(Reference?.Id ?? "", Name.Trim(), transport,
            Host.Trim().Trim('[', ']'), int.TryParse(Port, out var port) ? port : 0, Path.Trim().Trim('/'),
            new OfferedProxyTls(ServerName.Trim().Length == 0 ? Host.Trim().Trim('[', ']') : ServerName.Trim(),
                Pin.Trim(), true),
            new OfferedProxyAuth(_legacy ? _original?.Auth.Scheme ?? "legacy" : transport switch
                { "http" => "hmac-sha256-v2", "xhttp" => "vless", _ => "bearer" }, _legacy ? Credential : Credential.Trim()),
            DateTimeOffset.MinValue, transport == "xhttp" ? _original?.Xhttp : null,
            DraftTargets(), transport == "http" ? new OfferedProxyHttp(2, method, method == "GET" ? "header" : "body") : null,
            transport == "quic" ? new OfferedProxyQuic(1, "webtransport", "framed-stream") : null);
    }

    private ProxyTargets? DraftTargets()
    {
        if (_legacy)
        {
            return _original?.Targets;
        }

        if (!Restricted)
        {
            return null;
        }

        try
        {
            return ProxyTargetRules.Parse(TargetsText, restricted: true);
        }
        catch (ArgumentException)
        {
            return new ProxyTargets(true, []);
        }
    }

    public bool CanCommit()
    {
        var draft = Draft();
        var key = draft.Name.Length is < 1 or > 64 ? "Proxies_InvalidName"
            : Uri.CheckHostName(draft.Host) == UriHostNameType.Unknown ? "Proxies_InvalidHost"
            : draft.Port is < 1 or > 65535 ? "Transport_InvalidPort"
            : !_legacy && (draft.Path.Length is < 1 or > 64 || !draft.Path.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_')) ? "Proxies_InvalidPath"
            : ModernEditable && UseCertificate && Uri.CheckHostName(draft.Tls.ServerName) == UriHostNameType.Unknown ? "Proxies_InvalidSni"
            : ModernEditable && UseCertificate && draft.Tls.Pin.Length != 0 && (draft.Tls.Pin.Length != 64 || !draft.Tls.Pin.All(char.IsAsciiHexDigit)) ? "Proxies_InvalidPin"
            : !_legacy && (!ProxyTransportModes.Valid(draft)) ? "Proxies_InvalidCredential"
            : !ProxyTargetRules.Valid(draft.Targets) ? "Proxies_InvalidTargets"
            : string.Empty;
        StatusMessage = key.Length > 0 ? Loc.Instance.Get(key) : string.Empty;
        return Editable && key.Length == 0;
    }

    public void CaptureBaseline()
    {
        _baseline = Draft();
        IsDirty = Editable && IsNew;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Revert()
    {
        if (_baseline is not { } baseline)
        {
            return;
        }

        _applying = true;
        Name = baseline.Name;
        TransportIndex = Index(baseline.Transport);
        MethodIndex = baseline.Http?.Method == "POST" ? 1 : 0;
        UseCertificate = ProxyTransportModes.UsesCertificate(baseline);
        Host = baseline.Host;
        Port = baseline.Port.ToString(CultureInfo.InvariantCulture);
        Path = baseline.Path;
        ServerName = baseline.Tls.ServerName;
        Pin = baseline.Tls.Pin;
        Credential = baseline.Auth.Credential;
        Restricted = baseline.Targets?.Restricted == true;
        TargetsText = string.Join(Environment.NewLine, baseline.Targets?.Destinations.Select(ProxyTargetRules.Format) ?? []);
        _applying = false;
        StatusMessage = string.Empty;
        Changed();
    }

    public async Task<bool> CommitAsync()
    {
        if (!CanCommit())
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSaveTransportProxy,
                [Reference?.Id ?? "", JsonSerializer.Serialize(Draft(), IpcJson.Options)]));
            if (TransportProxyWords.Read<ProxyReference>(ack) is not { } reference)
            {
                StatusMessage = TransportProxyWords.Fault(ack.Message);
                return false;
            }

            Reference = reference;
            CaptureBaseline();
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(Title));
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyAvailability(TransportProxyProbeResult result)
    {
        _availability = result.Availability;
        OnPropertyChanged(nameof(Availability));
        StatusMessage = result.Fault.Length == 0 ? string.Empty : TransportProxyWords.Fault(result.Fault);
    }

    public void ApplySummary(TransportProxySummary? summary)
    {
        _missing = summary is null || summary.Missing;
        _availability = summary?.Availability ?? TransportProxyAvailability.Unknown;
        if (summary is not null)
        {
            if (summary.Sources.Count > 0 || !summary.Missing)
            {
                _source = string.Join(", ", summary.Sources);
            }
            if (!Editable)
            {
                _applying = true;
                Name = summary.Name;
                TransportIndex = Index(summary.Transport);
                MethodIndex = summary.Http?.Method == "POST" ? 1 : 0;
                UseCertificate = summary.UseCertificate;
                Host = summary.Host;
                Port = summary.Port.ToString(CultureInfo.InvariantCulture);
                Path = summary.Path;
                Restricted = summary.Targets?.Restricted == true;
                TargetsText = string.Join(Environment.NewLine, summary.Targets?.Destinations.Select(ProxyTargetRules.Format) ?? []);
                _applying = false;
            }
        }

        RefreshWords();
    }

    public void RefreshWords()
    {
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(CredentialLabel));
        OnPropertyChanged(nameof(Title));
    }
}
