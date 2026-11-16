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
    private int _checkRevision;
    private bool _verified;
    private string _definitionStamp = string.Empty;
    public string OwnerConfig { get; }
    public bool IsVerified => _verified;
    public TransportProxyProbeResult? LastProbeResult { get; private set; }
    public bool ShowOwner => Editable && OwnerConfig.Length > 0;
    public bool HasCredential => (_baseline ?? _original)?.Auth.Credential.Length > 0 || _summaryHasCredential;
    private bool _summaryHasCredential;
    public string CredentialPlaceholder => Anonymous ? "—" : HasCredential ? "••••••••" : Loc.Instance.Get("Proxies_EnterCredential");
    private string? _scope;
    private bool _cdn;
    public string ScopeText => Loc.Instance.Get(_scope == "local" ? "Proxies_ScopeLocal"
        : Restricted ? "Proxies_ScopeRestricted" : "Proxies_ScopeAny");
    public bool ShowLock => !Editable;

    public ProxyReference? Reference { get; private set; }
    public bool IsNew => Reference is null;
    public bool Editable { get; }
    public bool ModernEditable => Editable && !_legacy;
    public bool ShowCredential => true;
    public bool ShowTls => true;
    public bool ShowHttpMethod => TransportIndex == 1;
    public bool CertificateEditable => false;
    public bool ShowTargets => !Editable && !_legacy;
    public bool LocalSource => _local;
    public bool ShowAnonymous => ModernEditable || Anonymous;
    public string Protocol => TransportProxyPresentation.Transport(TransportIndex switch
        { 1 => "http", 2 => "quic", 3 => "xhttp", _ => "websocket" }, MethodIndex == 1 ? "POST" : "GET");
    public bool IsHttpGet => (TransportIndex is 1 or 3) && MethodIndex == 0;
    public bool Cdn => _cdn;
    public string Source => _local ? Loc.Instance.Get("Proxies_Local")
        : _source.Length > 0 ? _source : Loc.Instance.Get("Proxies_Server");
    public string Availability => TransportProxyWords.Availability(_availability, _missing);
    public string[] Transports { get; private set; } = ["WebSocket", "HTTP", "QUIC"];
    public string[] Methods { get; } = ["GET", "POST"];
    public string CredentialLabel => Loc.Instance.Get(_legacy ? "Proxies_BasicCredential" : "Proxies_BearerCredential");
    public string Title => _cdn ? "CDN" : Loc.Instance.Get("Proxies_EditorTitle");

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
    [ObservableProperty]
    private bool _compact;
    [ObservableProperty]
    private bool _anonymous;

    /// <summary>
    /// ctor
    /// </summary>
    public TransportProxyEditorViewModel(IAgentConnection connection, CatalogTransportProxy? local = null, TransportProxySummary? summary = null,
        string ownerConfig = "")
    {
        _connection = connection;
        if (local is not null)
        {
            local = local with { Proxy = ProxyTransportModes.Normalize(local.Proxy) };
        }
        _legacy = local?.Legacy == true || summary?.Legacy == true;
        _original = local?.Proxy;
        OwnerConfig = local?.OwnerConfig ?? ownerConfig;
        _summaryHasCredential = summary?.HasToken == true;
        _definitionStamp = summary?.DefinitionStamp ?? string.Empty;
        _scope = local?.Proxy.Scope ?? summary?.Scope;
        _cdn = local?.Proxy.Cdn ?? summary?.Cdn ?? false;
        _anonymous = local?.Proxy.Anonymous ?? summary?.Anonymous ?? false;
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
        _useCertificate = local?.Proxy.Tls.Enabled ?? summary?.UseCertificate ?? true;
        _host = local?.Proxy.Host ?? summary?.Host ?? string.Empty;
        _port = (local?.Proxy.Port ?? summary?.Port ?? 443).ToString(CultureInfo.InvariantCulture);
        _path = local?.Proxy.Path ?? summary?.Path ?? string.Empty;
        _serverName = local?.Proxy.Tls.ServerName ?? summary?.Tls?.ServerName ?? string.Empty;
        _pin = local?.Proxy.Tls.Pin ?? summary?.Tls?.Pin ?? string.Empty;
        _credential = string.Empty;
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
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(IsHttpGet));
    }
    partial void OnMethodIndexChanged(int value)
    {
        Changed();
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(IsHttpGet));
    }
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
    partial void OnAnonymousChanged(bool value)
    {
        Changed();
        OnPropertyChanged(nameof(CredentialPlaceholder));
        OnPropertyChanged(nameof(ShowAnonymous));
    }

    private void Changed()
    {
        if (_applying)
        {
            return;
        }

        InvalidateVerification();
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
        var credential = Anonymous ? transport is "http" or "xhttp" ? "f4514728-b141-4b09-a1fe-4a5a5f83cf51" : ""
            : Credential.Length == 0 ? (_baseline ?? _original) is { Anonymous: false } retained ? retained.Auth.Credential : ""
            : _legacy ? Credential : Credential.Trim();
        return new OfferedProxy(Reference?.Id ?? "draft", Name.Trim().Length > 0 ? Name.Trim() : Host.Trim()[..Math.Min(64, Host.Trim().Length)], transport,
            Host.Trim().Trim('[', ']'), int.TryParse(Port, out var port) ? port : 0, Path.Trim().Trim('/'),
            new OfferedProxyTls(ServerName.Trim().Length == 0 ? Host.Trim().Trim('[', ']') : ServerName.Trim(),
                Pin.Trim(), _original?.Tls.Enabled ?? true),
            new OfferedProxyAuth(_legacy ? _original?.Auth.Scheme ?? "legacy" : transport switch
                { "http" => "hmac-sha256-v2", "xhttp" => "vless", _ => "bearer" },
                credential,
                _original is not null ? _original.Auth.Header : _legacy ? null : "Authorization"),
            DateTimeOffset.MinValue, transport == "xhttp" ? _original?.Xhttp : null,
            DraftTargets(), transport == "http" ? new OfferedProxyHttp(2, method, method == "GET" ? "header" : "body") : null,
            transport == "quic" ? new OfferedProxyQuic(1, "webtransport", "framed-stream") : null,
            _original?.Scope, Anonymous, _original?.Cdn ?? false);
    }

    private ProxyTargets? DraftTargets()
    {
        if (_legacy || Editable && OwnerConfig.Length > 0)
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
            : Restricted && ModernEditable && OwnerConfig.Length == 0 && draft.Targets?.Destinations.Count == 0
                || !ProxyTargetRules.Valid(draft.Targets) ? "Proxies_InvalidTargets"
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
        Credential = string.Empty;
        Anonymous = baseline.Anonymous;
        Restricted = baseline.Targets?.Restricted == true;
        TargetsText = string.Join(Environment.NewLine, baseline.Targets?.Destinations.Select(ProxyTargetRules.Format) ?? []);
        _applying = false;
        StatusMessage = string.Empty;
        Changed();
    }

    public async Task<bool> CommitAsync()
    {
        if (!CanCommit() || !IsVerified)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSaveTransportProxy,
                OwnerConfig.Length > 0 ? [Reference?.Id ?? "", JsonSerializer.Serialize(Draft(), IpcJson.Options), OwnerConfig]
                    : [Reference?.Id ?? "", JsonSerializer.Serialize(Draft(), IpcJson.Options)]));
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
        StatusMessage = result.Fault.Length == 0 ? string.Empty : TransportProxyWords.Fault(result.Fault, result.Diagnostic);
    }

    public void ApplySummary(TransportProxySummary? summary)
    {
        if (summary?.DefinitionStamp != _definitionStamp || summary?.Missing == true)
        {
            InvalidateVerification();
            _definitionStamp = summary?.DefinitionStamp ?? string.Empty;
        }
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
                ServerName = summary.Tls?.ServerName ?? string.Empty;
                Pin = summary.Tls?.Pin ?? string.Empty;
                _summaryHasCredential = summary.HasToken;
                Anonymous = summary.Anonymous;
                _scope = summary.Scope;
                _cdn = summary.Cdn;
                Restricted = summary.Targets?.Restricted == true;
                TargetsText = string.Join(Environment.NewLine, summary.Targets?.Destinations.Select(ProxyTargetRules.Format) ?? []);
                _applying = false;
            }
        }

        RefreshWords();
    }

    public void InvalidateVerification()
    {
        _checkRevision++;
        _verified = false;
        LastProbeResult = null;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(IsVerified));
    }

    public async Task CheckAsync(string config, ProxyDestination? target = null)
    {
        InvalidateVerification();
        if (_missing || config.Length == 0 || Editable && !CanCommit())
        {
            return;
        }

        var revision = _checkRevision;
        var command = Editable
            ? new IpcCommand(IpcContract.OpProbeTransportProxyDraft, [config, Reference?.Id ?? "", JsonSerializer.Serialize(Draft(), IpcJson.Options)])
            : new IpcCommand(IpcContract.OpProbeTransportProxy, [JsonSerializer.Serialize(Reference, IpcJson.Options), config]);
        if (target is not null)
        {
            command = command with { Args = [.. command.Args, target.Host, target.Port.ToString(CultureInfo.InvariantCulture)] };
        }
        var ack = await _connection.SendCommandAsync(command);
        if (revision != _checkRevision)
        {
            return;
        }

        if (TransportProxyWords.Read<TransportProxyProbeResult>(ack) is not { } result)
        {
            StatusMessage = TransportProxyWords.Fault(ack.Message);
            return;
        }

        LastProbeResult = result;
        ApplyAvailability(result);
        _verified = result.Availability == TransportProxyAvailability.Available && result.Fault.Length == 0;
        if (_verified)
        {
            StatusMessage = Loc.Instance.Get("Proxies_CheckPassed");
        }

        OnPropertyChanged(nameof(IsVerified));
    }

    public void RefreshWords()
    {
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(CredentialLabel));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CredentialPlaceholder));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(IsHttpGet));
        OnPropertyChanged(nameof(Cdn));
        OnPropertyChanged(nameof(ShowAnonymous));
    }
}
