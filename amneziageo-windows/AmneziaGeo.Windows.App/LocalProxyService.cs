using System.Diagnostics;
using AmneziaGeo.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Local proxy of this machine: holds the listener to what the settings say and opens the firewall for as long
/// as the local network is allowed in. What the proxy opens leaves like any other socket here, so the routing
/// table decides whether it rides the tunnel.
/// </summary>
internal sealed class LocalProxyService(SettingsStore settings, ILogger<LocalProxyService> logger) : BackgroundService
{
    private const string RuleName = "AmneziaGeo local proxy";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly LocalProxyServer _server = new(new DirectProxyOutbound(), line => logger.LogInformation("{Line}", line));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalProxyOptions _applied = new();

    /// <summary>
    /// Whether the listener is up.
    /// </summary>
    public bool Running => _server.Running;

    /// <summary>
    /// Why the listener is down; empty while it holds.
    /// </summary>
    public string Error => _server.Error;

    /// <summary>
    /// Address other machines reach the proxy at; empty while it stays on this machine.
    /// </summary>
    public string Address => _server.Running && _applied.AllowLan ? LocalProxyServer.LanAddress("AmneziaGeo") : string.Empty;

    /// <summary>
    /// Reads the settings and moves the listener to them.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wanted = (await settings.LoadAsync(ct).ConfigureAwait(false)).Proxy();
            if (wanted == _applied)
            {
                return;
            }

            _applied = wanted;
            _server.Apply(wanted);
            Firewall(wanted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the local proxy could not take the settings");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _server.Stop();
            Firewall(new LocalProxyOptions());
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _server.Dispose();
        _gate.Dispose();
        base.Dispose();
    }

    // The rule is rewritten whole on every change, so the ports it names are the ports in force.
    private void Firewall(LocalProxyOptions options)
    {
        Netsh($"advfirewall firewall delete rule name=\"{RuleName}\"");
        if (!options.Enabled || !options.AllowLan)
        {
            return;
        }

        var ports = string.Join(',', options.Ports);
        if (Netsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={ports} profile=private,domain"))
        {
            logger.LogInformation("firewall opened for the local proxy on {Ports}", ports);
        }
    }

    private bool Netsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the firewall rule of the local proxy could not be written");
            return false;
        }
    }

}
