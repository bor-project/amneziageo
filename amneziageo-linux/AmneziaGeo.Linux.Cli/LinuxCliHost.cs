using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// The console on a Linux box: a unix socket, a systemd unit and a tun device.
/// </summary>
internal sealed class LinuxCliHost : ICliHost
{
    /// <summary>
    /// Unix socket the named pipe maps to on Linux.
    /// </summary>
    public const string SocketPath = "/tmp/CoreFxPipe_" + IpcContract.PipeName;

    /// <inheritdoc/>
    public string ExeName => "amneziageo";

    /// <inheritdoc/>
    public string ExtraUsage => """
        service
          daemon install [--data <dir>] [--iface <name>] [--engine <path>] [--print]
          daemon uninstall | start | stop | restart | status | logs [--follow]

        full-screen console
          tui                               menu-driven configuration over SSH
        """;

    /// <inheritdoc/>
    public TextReader? StandardInput => Console.In;

    /// <inheritdoc/>
    public async Task<IAgentLink?> ConnectAsync(TimeSpan commandTimeout, TimeSpan connectWait, CancellationToken ct)
    {
        var link = new PipeAgentLink(commandTimeout, ct);
        if (await link.ConnectAsync(connectWait).ConfigureAwait(false))
        {
            return link;
        }

        link.Dispose();
        return null;
    }

    /// <inheritdoc/>
    public string UnreachableHint()
    {
        if (!File.Exists(SocketPath))
        {
            return $"the agent is not running: {SocketPath} does not exist";
        }

        return $"could not talk to the agent on {SocketPath}; check its permissions and that the agent is alive";
    }

    /// <inheritdoc/>
    public Task<int>? TryRunLocalAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        args[0] == "daemon" ? DaemonCommands.RunAsync([.. args.Skip(1)]) : null;

    /// <inheritdoc/>
    public Task<int>? TryRunWithAgentAsync(IAgentLink agent, IReadOnlyList<string> args, CancellationToken ct) =>
        args[0] == "tui" ? Tui.TuiApp.RunAsync(agent) : null;

    /// <inheritdoc/>
    public IReadOnlyList<DoctorCheck> DoctorChecks(StatusSnapshot snapshot) =>
    [
        new("control socket", File.Exists(SocketPath), SocketPath),
        new("tun device", File.Exists("/dev/net/tun"), "/dev/net/tun"),
        new("iproute2", Which("ip") is not null, Which("ip") ?? "ip not found in PATH"),
        new("systemd unit", Systemd.Exists, Systemd.Exists ? Systemd.State() : $"{Systemd.UnitPath} not installed"),
    ];

    private static string? Which(string binary)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/sbin:/usr/bin:/sbin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, binary);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
