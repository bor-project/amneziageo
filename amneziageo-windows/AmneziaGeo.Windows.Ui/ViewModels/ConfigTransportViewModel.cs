using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// The WebSocket (UDP-over-TCP / wstunnel) transport settings shown on a config's management page: a
/// toggle, the server address (a bare host or a full <c>wss://[user:pass@]host:port/token</c> URL that
/// carries optional auth in one string), the TLS port, and a server-setup hint. Saving sends set-websocket.
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

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigTransportViewModel(AgentConnection connection, string name, string endpoint, bool useWebSocket, string webSocketHost, int webSocketPort)
    {
        _connection = connection;
        ConfigName = name;
        _endpoint = endpoint;
        _useWebSocket = useWebSocket;
        // Default the host field to the config's own Endpoint host so the common case (wstunnel on the
        // same server) needs no input; the user can override it with a full URL for a separate WS front.
        _webSocketHost = string.IsNullOrWhiteSpace(webSocketHost) ? EndpointHost(endpoint) : webSocketHost;
        _webSocketPort = webSocketPort.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The configuration name being edited.</summary>
    public string ConfigName { get; }

    /// <summary>
    /// The exact wstunnel server command for this config's server, so the user can stand up the matching
    /// endpoint. With a token (path in the URL) it shows --restrict-http-upgrade-path-prefix; without one
    /// it restricts the destination instead (the two flags do not combine on the wstunnel CLI).
    /// </summary>
    public string ServerHint
    {
        get
        {
            var awgPort = EndpointPort(_endpoint);
            var fallbackPort = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : 443;
            var (wsPort, token, hasCreds) = ParseWsField(WebSocketHost, fallbackPort);
            var restrictLine = string.IsNullOrEmpty(token)
                ? $"  --restrict-to 127.0.0.1:{awgPort} \\\n"
                : $"  --restrict-http-upgrade-path-prefix {token} \\\n";
            var hint =
                $"На сервере: установите wstunnel и запустите его как службу с командой\n\n" +
                $"wstunnel server wss://0.0.0.0:{wsPort} \\\n" +
                restrictLine +
                $"  --tls-certificate <fullchain.pem> --tls-private-key <privkey.pem>\n\n" +
                $"Где fullchain/privkey — уже имеющийся сертификат сервера (тот, что у x-ui / no-ip). " +
                $"Откройте TCP {wsPort} в фаерволе. Клиент завернёт UDP AmneziaWG в этот WebSocket.";
            if (hasCreds)
            {
                hint += "\n\nДля логина (user:pass) серверу нужен --restrict-config <yaml> с проверкой заголовка Authorization (см. доки wstunnel).";
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
                StatusMessage = "Укажите корректный порт WebSocket (1–65535).";
                return;
            }

            var wsPort = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 443;
            // Persist the host only when it differs from the config's own Endpoint host; an empty value
            // means "reuse the Endpoint host" on the agent side.
            var host = string.Equals(WebSocketHost.Trim(), EndpointHost(_endpoint), StringComparison.OrdinalIgnoreCase) ? string.Empty : WebSocketHost.Trim();
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetWebSocket,
                [ConfigName, UseWebSocket ? "on" : "off", wsPort.ToString(CultureInfo.InvariantCulture), host]));
            StatusMessage = ack.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (int Port, string Token, bool HasCreds) ParseWsField(string field, int fallbackPort)
    {
        var value = field?.Trim() ?? string.Empty;
        if (value.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            var port = uri.Port > 0 ? uri.Port : fallbackPort;
            return (port, uri.AbsolutePath.Trim('/'), !string.IsNullOrEmpty(uri.UserInfo));
        }

        return (fallbackPort, string.Empty, false);
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
