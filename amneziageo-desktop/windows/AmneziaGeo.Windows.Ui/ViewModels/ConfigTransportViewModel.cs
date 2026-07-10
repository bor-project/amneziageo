using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The WebSocket (UDP-over-TCP / wstunnel) transport settings for a config: a toggle, the server address, the TLS port, an authorization mode (none / basic login+password / path token) with its inputs, and a server-setup hint. The mode + inputs are folded into one stored address string on save and parsed back on load. Saving sends set-websocket.
/// </summary>
internal sealed partial class ConfigTransportViewModel : ViewModelBase, IEditScope
{
    private readonly AgentConnection _connection;
    private readonly string _endpoint;

    // Baseline captured on load / commit / import; the transport is dirty when a field differs from it (#143).
    private bool _baseUseWebSocket;
    private string _baseWebSocketHost = string.Empty;
    private string _baseWebSocketPort = string.Empty;
    private int _baseAuthMode;
    private string _baseWebSocketUser = string.Empty;
    private string _baseWebSocketPassword = string.Empty;
    private string _baseWebSocketToken = string.Empty;
    private string _baseMtu = string.Empty;

    [ObservableProperty]
    private bool _useWebSocket;

    [ObservableProperty]
    private string _webSocketHost = string.Empty;

    [ObservableProperty]
    private string _webSocketPort = "443";

    // Authorization mode: 0 = none, 1 = basic, 2 = path token.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBasicAuth))]
    [NotifyPropertyChangedFor(nameof(IsTokenAuth))]
    private int _authMode;

    [ObservableProperty]
    private string _webSocketUser = string.Empty;

    [ObservableProperty]
    private string _webSocketPassword = string.Empty;

    [ObservableProperty]
    private string _webSocketToken = string.Empty;

    [ObservableProperty]
    private string _mtu = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigTransportViewModel(AgentConnection connection, string name, string endpoint, bool useWebSocket, string webSocketHost, int webSocketPort, int mtu)
    {
        _connection = connection;
        ConfigName = name;
        _endpoint = endpoint;
        _useWebSocket = useWebSocket;
        _mtu = mtu > 0 ? mtu.ToString(CultureInfo.InvariantCulture) : "1420";

        // Parse the stored address; default the host to the config's Endpoint host.
        var (host, port, user, password, token, mode) = ParseStored(webSocketHost);
        _webSocketHost = string.IsNullOrWhiteSpace(host) ? EndpointHost(endpoint) : host;
        _authMode = mode;
        _webSocketUser = user;
        _webSocketPassword = password;
        _webSocketToken = token;
        _webSocketPort = (port > 0 ? port : webSocketPort).ToString(CultureInfo.InvariantCulture);

        // Seeded (backing fields set, no OnChanged fired): this state is the clean baseline (#143).
        CaptureBaseline();
    }

    // Dirty tracking suppressed while applying an import / reverting so those bulk field writes do not flip the
    // flag mid-way (#143). Field changes mark the item dirty; the header Save commits, the header Cancel reverts.
    private bool _applying;

    partial void OnUseWebSocketChanged(bool value) => MarkDirty();

    partial void OnWebSocketHostChanged(string value) => MarkDirty();

    partial void OnWebSocketPortChanged(string value) => MarkDirty();

    partial void OnAuthModeChanged(int value) => MarkDirty();

    partial void OnWebSocketUserChanged(string value) => MarkDirty();

    partial void OnWebSocketPasswordChanged(string value) => MarkDirty();

    partial void OnWebSocketTokenChanged(string value) => MarkDirty();

    partial void OnMtuChanged(string value) => MarkDirty();

    /// <inheritdoc />
    public bool IsDirty { get; private set; }

    /// <inheritdoc />
    public event EventHandler? DirtyChanged;

    private void MarkDirty()
    {
        if (_applying)
        {
            return;
        }

        // Any edit clears a stale validation / status line (#3).
        StatusMessage = string.Empty;

        var dirty = UseWebSocket != _baseUseWebSocket
            || !string.Equals(WebSocketHost, _baseWebSocketHost, StringComparison.Ordinal)
            || !string.Equals(WebSocketPort, _baseWebSocketPort, StringComparison.Ordinal)
            || AuthMode != _baseAuthMode
            || !string.Equals(WebSocketUser, _baseWebSocketUser, StringComparison.Ordinal)
            || !string.Equals(WebSocketPassword, _baseWebSocketPassword, StringComparison.Ordinal)
            || !string.Equals(WebSocketToken, _baseWebSocketToken, StringComparison.Ordinal)
            || !string.Equals(Mtu, _baseMtu, StringComparison.Ordinal);
        if (dirty != IsDirty)
        {
            IsDirty = dirty;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void CaptureBaseline()
    {
        _baseUseWebSocket = UseWebSocket;
        _baseWebSocketHost = WebSocketHost ?? string.Empty;
        _baseWebSocketPort = WebSocketPort ?? string.Empty;
        _baseAuthMode = AuthMode;
        _baseWebSocketUser = WebSocketUser ?? string.Empty;
        _baseWebSocketPassword = WebSocketPassword ?? string.Empty;
        _baseWebSocketToken = WebSocketToken ?? string.Empty;
        _baseMtu = Mtu ?? string.Empty;
        if (IsDirty)
        {
            IsDirty = false;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Revert()
    {
        _applying = true;
        try
        {
            UseWebSocket = _baseUseWebSocket;
            WebSocketHost = _baseWebSocketHost;
            WebSocketPort = _baseWebSocketPort;
            AuthMode = _baseAuthMode;
            WebSocketUser = _baseWebSocketUser;
            WebSocketPassword = _baseWebSocketPassword;
            WebSocketToken = _baseWebSocketToken;
            Mtu = _baseMtu;
            StatusMessage = string.Empty;
        }
        finally
        {
            _applying = false;
        }

        MarkDirty();
    }

    /// <summary>
    /// The configuration name being edited.
    /// </summary>
    public string ConfigName { get; private set; }

    /// <summary>
    /// Retargets these settings at a (newly-created) config name before committing them. Used by the config
    /// create form, which builds the transport editor before the config exists and only knows the final name
    /// at save time (#143).
    /// </summary>
    public void Retarget(string name) => ConfigName = name;

    /// <summary>
    /// True when the login+password auth mode is selected (mode 1).
    /// </summary>
    public bool IsBasicAuth => AuthMode == 1;

    /// <summary>
    /// True when the path-token auth mode is selected (mode 2).
    /// </summary>
    public bool IsTokenAuth => AuthMode == 2;

    /// <inheritdoc />
    public bool CanCommit()
    {
        if (UseWebSocket && (!int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535))
        {
            StatusMessage = Loc.Instance.Get("Transport_InvalidPort");
            return false;
        }

        // MTU: empty = default; validate 576-1500.
        var mtuVal = Mtu.Trim();
        if (mtuVal.Length > 0 && (!int.TryParse(mtuVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mtu) || mtu is < 576 or > 1500))
        {
            StatusMessage = Loc.Instance.Get("Transport_InvalidMtu");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Persists the transport settings through the agent (#143 header Save); returns whether it succeeded. An
    /// invalid port / MTU fails without a write and surfaces its reason, keeping the item dirty. Applies on
    /// reconnect.
    /// </summary>
    public async Task<bool> CommitAsync()
    {
        if (!CanCommit())
        {
            return false;
        }

        IsBusy = true;
        try
        {
            var wsPort = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 443;
            var mtuVal = Mtu.Trim();

            // Fold the host + auth mode / inputs into the stored address string. Collapse a bare host equal to the Endpoint host to empty; a URL form is sent verbatim.
            var composed = ComposeAddress(wsPort);
            var host = string.Equals(composed, EndpointHost(_endpoint), StringComparison.OrdinalIgnoreCase) ? string.Empty : composed;
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetWebSocket,
                [ConfigName, UseWebSocket ? "on" : "off", wsPort.ToString(CultureInfo.InvariantCulture), host, mtuVal]));
            StatusMessage = ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// A suggested file name when exporting these settings.
    /// </summary>
    public string SuggestedFileName => $"{ConfigName}-websocket.txt";

    /// <summary>
    /// Serialises the current WebSocket settings to a portable blob for copy / save.
    /// </summary>
    public string BuildTransferPayload()
    {
        var port = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : 443;
        return Services.PortableTransfer.EncodeWebSocket(UseWebSocket, port, ComposeAddress(port));
    }

    /// <summary>
    /// Applies an imported WebSocket blob to the editable fields; the change is held in the buffer and persisted
    /// by the header Save (#143), not auto-saved. Returns whether the text was a recognisable blob.
    /// </summary>
    public bool ApplyImport(string text)
    {
        if (!Services.PortableTransfer.TryDecodeWebSocket(text, out var enabled, out var port, out var host))
        {
            StatusMessage = Loc.Instance.Get("Transport_NotWebSocketBlob");
            return false;
        }

        _applying = true;
        try
        {
            UseWebSocket = enabled;
            var (parsedHost, parsedPort, user, password, token, mode) = ParseStored(host);
            WebSocketHost = string.IsNullOrWhiteSpace(parsedHost) ? EndpointHost(_endpoint) : parsedHost;
            WebSocketPort = (parsedPort > 0 ? parsedPort : port).ToString(CultureInfo.InvariantCulture);
            AuthMode = mode;
            WebSocketUser = user;
            WebSocketPassword = password;
            WebSocketToken = token;
        }
        finally
        {
            _applying = false;
        }

        StatusMessage = Loc.Instance.Get("Transport_Imported");
        MarkDirty();
        return true;
    }

    /// <summary>
    /// Builds the stored address from the host field and the selected auth mode: a bare host when no auth, a wss://user:pass@host:port URL for login+password (user/pass percent-escaped), or a wss://host:port/token URL for a token. The port is baked into any URL form so it is not lost to the wss default (443).
    /// </summary>
    private string ComposeAddress(int port)
    {
        var host = ExtractHost(WebSocketHost);
        if (host.Length == 0)
        {
            host = EndpointHost(_endpoint);
        }

        if (AuthMode == 1)
        {
            var user = WebSocketUser.Trim();
            if (user.Length > 0)
            {
                var userInfo = Uri.EscapeDataString(user);
                if (WebSocketPassword.Length > 0)
                {
                    userInfo += ":" + Uri.EscapeDataString(WebSocketPassword);
                }

                return $"wss://{userInfo}@{host}:{port}";
            }
        }
        else if (AuthMode == 2)
        {
            var token = WebSocketToken.Trim().Trim('/');
            if (token.Length > 0)
            {
                return $"wss://{host}:{port}/{token}";
            }
        }

        return host;
    }

    /// <summary>
    /// Splits a stored address into (host, port, user, password, token, mode). Accepts an empty value, a
    /// bare host, or a ws(s):// URL carrying optional basic-auth user info and/or a path token.
    /// </summary>
    private static (string Host, int Port, string User, string Password, string Token, int Mode) ParseStored(string? stored)
    {
        var value = stored?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return (string.Empty, 0, string.Empty, string.Empty, string.Empty, 0);
        }

        if (value.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            var token = uri.AbsolutePath.Trim('/');
            var userInfo = uri.UserInfo ?? string.Empty;
            if (userInfo.Length > 0)
            {
                var colon = userInfo.IndexOf(':');
                var user = colon >= 0 ? userInfo[..colon] : userInfo;
                var pass = colon >= 0 ? userInfo[(colon + 1)..] : string.Empty;
                return (uri.Host, uri.Port, Uri.UnescapeDataString(user), Uri.UnescapeDataString(pass), token, 1);
            }

            if (token.Length > 0)
            {
                return (uri.Host, uri.Port, string.Empty, string.Empty, token, 2);
            }

            return (uri.Host, uri.Port, string.Empty, string.Empty, string.Empty, 0);
        }

        return (value, 0, string.Empty, string.Empty, string.Empty, 0);
    }

    // Reduce a pasted ws(s):// URL to its host; a bare host passes through.
    private static string ExtractHost(string field)
    {
        var value = field?.Trim() ?? string.Empty;
        if (value.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host;
        }

        return value;
    }

    private static string EndpointHost(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon].Trim() : endpoint.Trim();
    }
}
