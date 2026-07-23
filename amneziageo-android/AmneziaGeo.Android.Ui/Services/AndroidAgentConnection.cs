using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AmneziaGeo.Android.Engine;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// In-process agent for the Android head: persists configs/profiles, projects status snapshots, and drives
/// the tunnel through <see cref="GeoVpnService"/>.
/// </summary>
internal sealed class AndroidAgentConnection : IAgentConnection
{
    private readonly Dictionary<string, string> _configs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _profiles = new(StringComparer.Ordinal);
    private readonly string _storePath;

    private string? _selectedTarget;
    private string? _boundTarget;
    private string _boundStatus = ConnectionStatus.Disconnected;
    private bool _active;
    private bool _connectFailed;
    private bool _started;
    private bool _disposed;

    public event Action? Connected;

    public event Action? Disconnected;

    public event Action<StatusSnapshot>? SnapshotReceived;

    /// <summary>
    /// ctor
    /// </summary>
    public AndroidAgentConnection()
    {
        var dir = Application.Context.FilesDir?.AbsolutePath ?? ".";
        _storePath = System.IO.Path.Combine(dir, "agent.json");
    }

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Load();
        GeoVpnService.StateChanged += OnVpnStateChanged;
        Connected?.Invoke();
        PushSnapshot();
    }

    public Task<IpcAck> SendCommandAsync(IpcCommand command) => DispatchAsync(command);

    public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => DispatchAsync(command);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GeoVpnService.StateChanged -= OnVpnStateChanged;
        if (_started)
        {
            Disconnected?.Invoke();
        }
    }

    private async Task<IpcAck> DispatchAsync(IpcCommand command)
    {
        var args = command.Args;
        switch (command.Op)
        {
            case IpcContract.OpImportConfig:
            case IpcContract.OpEditConfig:
                if (args.Count < 2)
                {
                    return Fail();
                }

                _configs[args[0]] = args[1];
                Save();
                PushSnapshot();
                return Ok();

            case IpcContract.OpAddProfile:
                if (args.Count < 1)
                {
                    return Fail();
                }

                _profiles[args[0]] = args.Count > 1 ? args[1] : string.Empty;
                Save();
                PushSnapshot();
                return Ok();

            case IpcContract.OpRemoveConfig:
                if (args.Count > 0)
                {
                    _configs.Remove(args[0]);
                    Save();
                    PushSnapshot();
                }

                return Ok();

            case IpcContract.OpRemoveProfile:
                if (args.Count > 0)
                {
                    _profiles.Remove(args[0]);
                    Save();
                    PushSnapshot();
                }

                return Ok();

            case IpcContract.OpGetConfig:
                return args.Count > 0 && _configs.TryGetValue(args[0], out var text)
                    ? new IpcAck(true, text)
                    : Fail();

            case IpcContract.OpSelectProfile:
                _selectedTarget = args.Count > 0 ? args[0] : null;
                PushSnapshot();
                return Ok();

            case IpcContract.OpSetConnection:
                return await SetConnectionAsync(args.Count > 0 ? args[0] : string.Empty);

            default:
                return new IpcAck(false, Loc.Instance.Get("Android_EngineNotReady"));
        }
    }

    private async Task<IpcAck> SetConnectionAsync(string desired)
    {
        if (desired == "disconnect")
        {
            StartService(GeoVpnService.ActionDisconnect, null, null, foreground: false);
            return Ok();
        }

        var configName = _selectedTarget is not null && _profiles.TryGetValue(_selectedTarget, out var bound) && bound.Length > 0
            ? bound
            : _selectedTarget;
        if (configName is null || !_configs.TryGetValue(configName, out var configText))
        {
            _connectFailed = true;
            PushSnapshot();
            return new IpcAck(false, "config missing");
        }

        var granted = await EnsureVpnPermissionAsync();
        if (!granted)
        {
            return new IpcAck(false, "vpn permission denied");
        }

        _connectFailed = false;
        StartService(GeoVpnService.ActionConnect, configText, _selectedTarget, foreground: true);
        return Ok();
    }

    private static async Task<bool> EnsureVpnPermissionAsync()
    {
        var prepare = VpnService.Prepare(Application.Context);
        if (prepare is null)
        {
            return true;
        }

        var activity = MainActivity.Current;
        return activity is not null && await activity.RequestVpnPermissionAsync(prepare);
    }

    private static void StartService(string action, string? config, string? name, bool foreground)
    {
        var context = Application.Context;
        var intent = new Intent(context, typeof(GeoVpnService));
        intent.SetAction(action);
        if (config is not null)
        {
            intent.PutExtra(GeoVpnService.ExtraConfig, config);
        }

        if (name is not null)
        {
            intent.PutExtra(GeoVpnService.ExtraName, name);
        }

        if (foreground && Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    private void OnVpnStateChanged(VpnStage stage, string? detail)
    {
        switch (stage)
        {
            case VpnStage.Connecting:
                _active = true;
                _boundStatus = ConnectionStatus.Connecting;
                _boundTarget = _selectedTarget;
                break;
            case VpnStage.Connected:
                _active = true;
                _boundStatus = ConnectionStatus.Connected;
                _boundTarget = _selectedTarget;
                _connectFailed = false;
                break;
            case VpnStage.Disconnected:
                _active = false;
                _boundStatus = ConnectionStatus.Disconnected;
                _boundTarget = null;
                break;
            case VpnStage.Failed:
                _active = false;
                _boundStatus = ConnectionStatus.Disconnected;
                _boundTarget = null;
                _connectFailed = true;
                break;
        }

        PushSnapshot();
    }

    private void PushSnapshot()
    {
        var configs = _configs
            .Select(kv => new ConfigEntry(kv.Key, WgConfigEditor.GetEndpoint(kv.Value) ?? string.Empty, false, StatusFor(kv.Key), []))
            .ToList();
        var profiles = _profiles
            .Select(kv => new ProfileEntry(kv.Key, StatusFor(kv.Key), kv.Value))
            .ToList();

        SnapshotReceived?.Invoke(new StatusSnapshot(
            AgentVersion: "Android preview",
            BoundTarget: _boundTarget,
            Configs: configs,
            Profiles: profiles,
            RoutingLists: [],
            Active: _active,
            BoundStatus: _boundStatus,
            SelectedTarget: _selectedTarget,
            Sources: [],
            ConnectFailed: _connectFailed,
            EngineVersion: string.Empty));
    }

    private string StatusFor(string target)
    {
        return _active && string.Equals(_boundTarget, target, StringComparison.Ordinal)
            ? ConnectionStatus.Connected
            : ConnectionStatus.Disconnected;
    }

    private void Load()
    {
        try
        {
            if (!System.IO.File.Exists(_storePath))
            {
                return;
            }

            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(_storePath));
            LoadMap(document.RootElement, "Configs", _configs);
            LoadMap(document.RootElement, "Profiles", _profiles);
        }
        catch (Exception)
        {
        }
    }

    private static void LoadMap(JsonElement root, string name, Dictionary<string, string> into)
    {
        if (!root.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                into[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }
        }
    }

    private void Save()
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("{\"Configs\":");
            AppendMap(builder, _configs);
            builder.Append(",\"Profiles\":");
            AppendMap(builder, _profiles);
            builder.Append('}');
            System.IO.File.WriteAllText(_storePath, builder.ToString());
        }
        catch (Exception)
        {
        }
    }

    private static void AppendMap(System.Text.StringBuilder builder, Dictionary<string, string> map)
    {
        builder.Append('{');
        var first = true;
        foreach (var entry in map)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(JsonSerializer.Serialize(entry.Key)).Append(':').Append(JsonSerializer.Serialize(entry.Value));
        }

        builder.Append('}');
    }

    private static IpcAck Ok() => new(true, string.Empty);

    private static IpcAck Fail() => new(false, string.Empty);
}
