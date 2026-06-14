using System.IO.Pipes;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Builds status snapshots and pushes them to connected UI clients over the status pipe.
/// </summary>
internal sealed class AgentStatusBroker(ConfigRepository configRepo, IStateStore store, ILogger<AgentStatusBroker> logger)
{
    private readonly List<PipeConnection> _clients = [];
    private readonly Lock _gate = new();
    private string? _lastJson;

    /// <summary>
    /// The balancer or single-config name the agent is bound to.
    /// </summary>
    public string? BoundTarget { get; set; }

    /// <summary>
    /// Handles a connected client: sends the current snapshot, then reads until the client disconnects.
    /// </summary>
    public async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        var connection = new PipeConnection(stream);
        lock (_gate)
        {
            _clients.Add(connection);
        }

        logger.LogInformation("status client connected");
        try
        {
            var json = await BuildJsonAsync(ct);
            await connection.SendAsync(json, ct);
            await DrainAsync(stream, ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "status client handler failed");
        }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(connection);
            }

            connection.Dispose();
            logger.LogInformation("status client disconnected");
        }
    }

    /// <summary>
    /// Pushes a fresh snapshot to all clients when it differs from the last one sent.
    /// </summary>
    public async Task BroadcastIfChangedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_clients.Count == 0)
            {
                return;
            }
        }

        var json = await BuildJsonAsync(ct);
        PipeConnection[] targets;
        lock (_gate)
        {
            if (json == _lastJson)
            {
                return;
            }

            _lastJson = json;
            targets = [.. _clients];
        }

        foreach (var target in targets)
        {
            try
            {
                await target.SendAsync(json, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task DrainAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }
        }
    }

    private async Task<string> BuildJsonAsync(CancellationToken ct)
    {
        var snapshot = await BuildSnapshotAsync(ct);
        return JsonSerializer.Serialize(new IpcEnvelope(IpcContract.SnapshotType, snapshot), IpcJson.Options);
    }

    private async Task<StatusSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var states = await store.ListBalancerStatesAsync(ct);
        var activeMembers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (state.ActiveMember is not null)
            {
                activeMembers[state.ActiveMember] = state.Status;
            }
        }

        var configs = new List<ConfigEntry>();
        foreach (var name in configRepo.List())
        {
            var geo = await store.GetTunnelGeoAsync(name, ct);
            var status = activeMembers.TryGetValue(name, out var groupStatus) ? MemberStatus(groupStatus) : ConnectionStatus.Idle;
            configs.Add(new ConfigEntry(name, ReadEndpoint(name), geo?.GeoSplit ?? false, status));
        }

        var balancers = new List<BalancerEntry>();
        foreach (var name in await store.ListBalancerNamesAsync(ct))
        {
            var balancer = await store.GetBalancerAsync(name, ct);
            if (balancer is null)
            {
                continue;
            }

            var state = states.FirstOrDefault(item => item.Group == name);
            balancers.Add(new BalancerEntry(
                name,
                balancer.Mode,
                state?.Status ?? ConnectionStatus.Disconnected,
                state?.ActiveMember,
                balancer.Members));
        }

        return new StatusSnapshot(Version(), BoundTarget, configs, balancers);
    }

    private static string MemberStatus(string groupStatus)
    {
        return groupStatus switch
        {
            "connected" or "degraded" => ConnectionStatus.Connected,
            "connecting" or "failover" => ConnectionStatus.Connecting,
            _ => ConnectionStatus.Idle,
        };
    }

    private static string ReadEndpoint(string name)
    {
        try
        {
            foreach (var line in File.ReadLines(TunnelPaths.ConfigFile(name)))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
                }
            }
        }
        catch (IOException)
        {
        }

        return string.Empty;
    }

    private static string Version()
    {
        return typeof(AgentStatusBroker).Assembly.GetName().Version?.ToString() ?? "0";
    }
}
