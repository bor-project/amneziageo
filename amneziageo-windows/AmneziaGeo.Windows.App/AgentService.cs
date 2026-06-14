using System.ServiceProcess;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Always-on Windows service hosting the balancer orchestrator for the active group.
/// </summary>
internal sealed class AgentService : ServiceBase
{
    private readonly BalancerGroup _group;
    private readonly int _connectTimeoutSeconds;
    private readonly int _deadThresholdSeconds;
    private readonly IStateStore _store;
    private readonly FileLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// ctor
    /// </summary>
    public AgentService(BalancerGroup group, int connectTimeoutSeconds, int deadThresholdSeconds, IStateStore store, FileLogger logger)
    {
        _group = group;
        _connectTimeoutSeconds = connectTimeoutSeconds;
        _deadThresholdSeconds = deadThresholdSeconds;
        _store = store;
        _logger = logger;
        ServiceName = TunnelPaths.AgentServiceName();
    }

    /// <inheritdoc/>
    protected override void OnStart(string[] args)
    {
        _logger.Log($"agent starting: group {_group.Name} ({_group.Members.Count} member(s))");
        var runner = new BalancerRunner(_group, _connectTimeoutSeconds, _deadThresholdSeconds, _store, _logger.Log);
        _loop = Task.Run(() => runner.RunAsync(_cts.Token));
    }

    /// <inheritdoc/>
    protected override void OnStop()
    {
        _logger.Log("agent stopping");
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
        }

        _logger.Log("agent stopped");
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
