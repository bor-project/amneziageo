using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Per-config geo split-tunnel settings: server (full tunnel) versus custom routing.
/// </summary>
internal sealed partial class ConfigSettingsViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly string _endpoint;

    [ObservableProperty]
    private bool _useCustom;

    [ObservableProperty]
    private string _ruleInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerHint))]
    private bool _useWebSocket;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerHint))]
    private string _webSocketHost = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerHint))]
    private string _webSocketPort = "443";

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigSettingsViewModel(AgentConnection connection, string name, string endpoint, bool geoSplit, IReadOnlyList<string> rules, bool useWebSocket, string webSocketHost, int webSocketPort)
    {
        _connection = connection;
        ConfigName = name;
        _endpoint = endpoint;
        _useCustom = geoSplit;
        _useWebSocket = useWebSocket;
        // Default the host field to the config's own Endpoint host so the common case (wstunnel on the
        // same server) needs no input; the user can override it for a separate WS front.
        _webSocketHost = string.IsNullOrWhiteSpace(webSocketHost) ? EndpointHost(endpoint) : webSocketHost;
        _webSocketPort = webSocketPort.ToString(CultureInfo.InvariantCulture);
        foreach (var rule in rules)
        {
            Rules.Add(rule);
        }
    }

    /// <summary>
    /// The exact wstunnel server command for this config's server, so the user can stand up the matching
    /// endpoint. The server-side AmneziaWG UDP port is taken from the config's own Endpoint; the listen
    /// port follows the WebSocket port field.
    /// </summary>
    public string ServerHint
    {
        get
        {
            var awgPort = EndpointPort(_endpoint);
            var wsPort = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : 443;
            return
                $"На сервере: установите wstunnel и запустите его как службу с командой\n\n" +
                $"wstunnel server wss://0.0.0.0:{wsPort} \\\n" +
                $"  --restrict-to 127.0.0.1:{awgPort} \\\n" +
                $"  --tls-certificate <fullchain.pem> --tls-private-key <privkey.pem>\n\n" +
                $"Где fullchain/privkey — уже имеющийся сертификат сервера (тот, что у x-ui / no-ip). " +
                $"Откройте TCP {wsPort} в фаерволе. Клиент завернёт UDP AmneziaWG в этот WebSocket.";
        }
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

    /// <summary>
    /// The configuration name being edited.
    /// </summary>
    public string ConfigName { get; }

    /// <summary>
    /// The split-tunnel rules (geo categories or custom domains/cidrs).
    /// </summary>
    public ObservableCollection<string> Rules { get; } = [];

    /// <summary>
    /// Geo category suggestions for the rule input, fetched from the agent.
    /// </summary>
    public ObservableCollection<string> GeoSuggestions { get; } = [];

    /// <summary>
    /// Loads the available geo categories from the agent for autocompletion.
    /// </summary>
    public async Task LoadSuggestionsAsync()
    {
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListGeo, []));
        if (!ack.Ok)
        {
            return;
        }

        foreach (var token in ack.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            GeoSuggestions.Add(token);
        }
    }

    /// <summary>
    /// Saves the settings through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        IsBusy = true;
        try
        {
            if (UseWebSocket && (!int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var validate) || validate is < 1 or > 65535))
            {
                StatusMessage = "Укажите корректный порт WebSocket (1–65535).";
                return false;
            }

            var geoArgs = new List<string> { ConfigName, UseCustom ? "on" : "off" };
            if (UseCustom)
            {
                geoArgs.AddRange(Rules);
            }

            var geoAck = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetGeo, geoArgs));
            if (!geoAck.Ok)
            {
                StatusMessage = geoAck.Message;
                return false;
            }

            var wsPort = int.TryParse(WebSocketPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 443;
            // Persist the host only when it differs from the config's own Endpoint host; an empty value
            // means "reuse the Endpoint host" on the agent side.
            var host = string.Equals(WebSocketHost.Trim(), EndpointHost(_endpoint), StringComparison.OrdinalIgnoreCase) ? string.Empty : WebSocketHost.Trim();
            var wsAck = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetWebSocket,
                [ConfigName, UseWebSocket ? "on" : "off", wsPort.ToString(CultureInfo.InvariantCulture), host]));
            StatusMessage = wsAck.Ok ? geoAck.Message : wsAck.Message;
            return wsAck.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        var text = RuleInput.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var rule = Normalize(text);
        if (!Rules.Contains(rule))
        {
            Rules.Add(rule);
        }

        RuleInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveRule(string rule)
    {
        Rules.Remove(rule);
    }

    private static string Normalize(string text)
    {
        if (text.Contains(':'))
        {
            return text;
        }

        return text.Contains('/') ? $"cidr:{text}" : $"domain:{text}";
    }
}
