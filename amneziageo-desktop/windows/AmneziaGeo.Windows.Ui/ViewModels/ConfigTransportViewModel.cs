using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The WebSocket (UDP-over-TCP / wstunnel) transport settings shown on a config's management page: a
/// toggle, the server address, the TLS port, an authorization mode (none / basic login+password / path
/// token) with its inputs, and a server-setup hint. The mode + inputs are folded into one stored address
/// string (a bare host, or a <c>wss://[user:pass@]host:port[/token]</c> URL) on save and parsed back on
/// load - the agent already understands both auth forms from that single field. Saving sends set-websocket.
/// </summary>
internal sealed partial class ConfigTransportViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly string _endpoint;

    [ObservableProperty]
    private bool _useWebSocket;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerHint))]
    private string _webSocketHost = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerHint))]
    private string _webSocketPort = "443";

    // Authorization mode: 0 = none, 1 = login + password (basic), 2 = path token. Drives which inputs
    // show and how the address is composed on save.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBasicAuth))]
    [NotifyPropertyChangedFor(nameof(IsTokenAuth))]
    [NotifyPropertyChangedFor(nameof(ServerHint))]
    private int _authMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerHint))]
    private string _webSocketUser = string.Empty;

    [ObservableProperty]
    private string _webSocketPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerHint))]
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
        _mtu = mtu > 0 ? mtu.ToString(CultureInfo.InvariantCulture) : "1280";

        // Split the stored address into the host plus its auth mode / inputs. Default the host field to
        // the config's own Endpoint host so the common case (wstunnel on the same server) needs no input.
        var (host, port, user, password, token, mode) = ParseStored(webSocketHost);
        _webSocketHost = string.IsNullOrWhiteSpace(host) ? EndpointHost(endpoint) : host;
        _authMode = mode;
        _webSocketUser = user;
        _webSocketPassword = password;
        _webSocketToken = token;
        _webSocketPort = (port > 0 ? port : webSocketPort).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The configuration name being edited.</summary>
    public string ConfigName { get; }

    /// <summary>True when the login+password auth mode is selected (mode 1).</summary>
    public bool IsBasicAuth => AuthMode == 1;

    /// <summary>True when the path-token auth mode is selected (mode 2).</summary>
    public bool IsTokenAuth => AuthMode == 2;

    /// <summary>
    /// The exact wstunnel server command for this config's server, so the user can stand up the matching
    /// endpoint. The auth mode tailors it: a token shows --restrict-http-upgrade-path-prefix (and the
    /// matching path goes in the client URL); login+password adds the credentials note; without either the
    /// destination is restricted instead. The token and --restrict-to flags do not combine on the CLI.
    /// </summary>
    public string ServerHint
    {
        get
        {
            var awgPort = EndpointPort(_endpoint);
            var wsPort = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : 443;
            var token = WebSocketToken.Trim().Trim('/');
            var restrictLine = IsTokenAuth && token.Length > 0
                ? $"  --restrict-http-upgrade-path-prefix {token} \\\n"
                : $"  --restrict-to 127.0.0.1:{awgPort} \\\n";
            var hint =
                Loc.Instance.Get("Transport_HintIntro") + "\n\n" +
                $"wstunnel server wss://0.0.0.0:{wsPort} \\\n" +
                restrictLine +
                $"  --tls-certificate <fullchain.pem> --tls-private-key <privkey.pem>\n\n" +
                Loc.Instance.Get("Transport_HintCert") + " " +
                Loc.Instance.Get("Transport_HintFirewall", wsPort);

            if (IsBasicAuth && WebSocketUser.Trim().Length > 0)
            {
                hint += "\n\n" + Loc.Instance.Get("Transport_HintBasicAuth");
            }
            else if (IsTokenAuth && token.Length > 0)
            {
                hint += "\n\n" + Loc.Instance.Get("Transport_HintTokenAuth");
            }

            return hint;
        }
    }

    /// <summary>
    /// Saves the transport settings through the agent; returns whether it succeeded. Applies on reconnect.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            if (UseWebSocket && (!int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var validate) || validate is < 1 or > 65535))
            {
                StatusMessage = Loc.Instance.Get("Transport_InvalidPort");
                return;
            }

            var wsPort = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 443;

            // MTU: empty = default 1280. Validate 576-1500 before sending.
            var mtuVal = Mtu.Trim();
            if (mtuVal.Length > 0 && (!int.TryParse(mtuVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mtu) || mtu is < 576 or > 1500))
            {
                StatusMessage = Loc.Instance.Get("Transport_InvalidMtu");
                return;
            }

            // Fold the host + auth mode / inputs into the single stored address string. An empty value
            // means "reuse the Endpoint host" on the agent side, so collapse a bare host that equals the
            // config's own Endpoint host to empty; a URL form (auth present) is always sent verbatim.
            var composed = ComposeAddress(wsPort);
            var host = string.Equals(composed, EndpointHost(_endpoint), StringComparison.OrdinalIgnoreCase) ? string.Empty : composed;
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetWebSocket,
                [ConfigName, UseWebSocket ? "on" : "off", wsPort.ToString(CultureInfo.InvariantCulture), host, mtuVal]));
            StatusMessage = ack.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A suggested file name when exporting these settings.</summary>
    public string SuggestedFileName => $"{ConfigName}-websocket.txt";

    /// <summary>
    /// Serialises the current WebSocket settings (enabled, port, composed address incl. auth) to a
    /// portable blob for copy / save - the same share flow a config has.
    /// </summary>
    public string BuildTransferPayload()
    {
        var port = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : 443;
        return Services.PortableTransfer.EncodeWebSocket(UseWebSocket, port, ComposeAddress(port));
    }

    /// <summary>
    /// Applies an imported WebSocket blob to the editable fields (the user still presses «Сохранить» to
    /// persist). Returns whether the text was a recognisable blob.
    /// </summary>
    public bool ApplyImport(string text)
    {
        if (!Services.PortableTransfer.TryDecodeWebSocket(text, out var enabled, out var port, out var host))
        {
            StatusMessage = Loc.Instance.Get("Transport_NotWebSocketBlob");
            return false;
        }

        UseWebSocket = enabled;
        var (parsedHost, parsedPort, user, password, token, mode) = ParseStored(host);
        WebSocketHost = string.IsNullOrWhiteSpace(parsedHost) ? EndpointHost(_endpoint) : parsedHost;
        WebSocketPort = (parsedPort > 0 ? parsedPort : port).ToString(CultureInfo.InvariantCulture);
        AuthMode = mode;
        WebSocketUser = user;
        WebSocketPassword = password;
        WebSocketToken = token;
        StatusMessage = Loc.Instance.Get("Transport_Imported");
        return true;
    }

    /// <summary>
    /// Builds the stored address from the host field and the selected auth mode: a bare host when no auth,
    /// a <c>wss://user:pass@host:port</c> URL for login+password (user/pass percent-escaped so symbols
    /// survive the URL; the agent unescapes them back), or a <c>wss://host:port/token</c> URL for a token.
    /// The port is baked into any URL form so it is not lost to the wss default (443).
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

    // The host the address is built around: a pasted ws(s):// URL is reduced to its host (its embedded
    // auth is superseded by the explicit mode/inputs); a bare host passes through unchanged.
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

    private static int EndpointPort(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        if (colon >= 0 && colon < endpoint.Length - 1
            && int.TryParse(endpoint[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return port;
        }

        return 9080;
    }

    private static string EndpointHost(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon].Trim() : endpoint.Trim();
    }
}
