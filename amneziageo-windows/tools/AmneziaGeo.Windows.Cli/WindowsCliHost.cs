using System.Diagnostics;
using AmneziaGeo.Cli;
using AmneziaGeo.Cli.Fleet;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.Cli;

/// <summary>
/// The console on Windows: a named pipe and the agent service.
/// </summary>
internal sealed class WindowsCliHost : ICliHost
{
    /// <summary>
    /// Service the installer registers the agent under.
    /// </summary>
    public const string ServiceName = "AmneziaGeoAgent";

    private const string _pipeDirectory = @"\\.\pipe\";

    /// <inheritdoc/>
    public string ExeName => "amneziageo";

    /// <inheritdoc/>
    public string ExtraUsage => $"""
        {FleetCommands.Usage}

        service
          service status | start | stop | restart
          The installer registers the service; installing it by hand needs an elevated
          'AmneziaGeo.Windows.App.exe agent-install <target>'.
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
        if (!PipeExists())
        {
            var state = ServiceState();
            return state == "not installed"
                ? $@"the agent is not running: no {_pipeDirectory}{IpcContract.PipeName} and no {ServiceName} service"
                : $@"the agent is not running: the {ServiceName} service is {state}; start it with 'amneziageo service start'";
        }

        return $@"could not talk to the agent on {_pipeDirectory}{IpcContract.PipeName}; run as the user whose library the agent serves";
    }

    /// <inheritdoc/>
    public Task<int>? TryRunLocalAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        args[0] == "service" ? Task.FromResult(Service([.. args.Skip(1)])) : null;

    /// <inheritdoc/>
    public Task<int>? TryRunWithAgentAsync(IAgentLink agent, IReadOnlyList<string> args, CancellationToken ct) =>
        FleetCommands.Claims(agent.Snapshot, args) ? FleetCommands.RunAsync(agent, args) : null;

    /// <inheritdoc/>
    public IReadOnlyList<DoctorCheck> DoctorChecks(StatusSnapshot snapshot)
    {
        var state = ServiceState();
        return
        [
            new("control pipe", PipeExists(), _pipeDirectory + IpcContract.PipeName),
            new("agent service", state == "RUNNING", $"{ServiceName}: {state}"),
            new("engine", snapshot.EngineVersion.Length > 0, snapshot.EngineVersion.Length > 0 ? snapshot.EngineVersion : "the build could not resolve the AmneziaWG version"),
        ];
    }

    private static int Service(IReadOnlyList<string> args)
    {
        if (args.Count != 1 || args[0] is not ("status" or "start" or "stop" or "restart"))
        {
            return Reply.Usage("usage: amneziageo service <status|start|stop|restart>");
        }

        if (args[0] == "status")
        {
            var state = ServiceState();
            if (Output.Json)
            {
                Output.AsJson(new { service = ServiceName, state, pipe = PipeExists() });
                return state == "RUNNING" ? Exit.Ok : Exit.Failed;
            }

            Output.Pairs([("service", ServiceName), ("state", state), ("control pipe", PipeExists() ? "present" : "missing")]);
            return state == "RUNNING" ? Exit.Ok : Exit.Failed;
        }

        if (args[0] == "restart")
        {
            Sc(["stop", ServiceName]);
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        var verb = args[0] == "restart" ? "start" : args[0];
        if (verb == "stop")
        {
            Output.Info("this drops the tunnel: the agent tears the interface down when it exits");
        }

        var result = Sc([verb, ServiceName]);
        if (result.Code != 0)
        {
            Output.Error(result.Output.Trim().Length > 0 ? result.Output.Trim() : $"sc {verb} failed with {result.Code}");
            return result.Output.Contains("5:", StringComparison.Ordinal) || result.Output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                ? Reply.Usage($"controlling {ServiceName} needs an elevated console")
                : Exit.Failed;
        }

        Output.Info($"{ServiceName} {verb} requested");
        return Exit.Ok;
    }

    private static bool PipeExists()
    {
        try
        {
            return Directory.EnumerateFiles(_pipeDirectory)
                .Any(path => path.EndsWith(IpcContract.PipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ServiceState()
    {
        var result = Sc(["query", ServiceName]);
        if (result.Code != 0)
        {
            return "not installed";
        }

        foreach (var line in result.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 0 ? words[^1] : "unknown";
        }

        return "unknown";
    }

    private static (int Code, string Output) Sc(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return (127, "could not start sc.exe");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (127, $"sc.exe: {ex.Message}");
        }
    }
}
