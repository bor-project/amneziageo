using System.Runtime.InteropServices;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Headless Linux agent entry point.
/// </summary>
public static class Program
{
    private const string _usage = """
        AmneziaGeo agent (headless).

        usage: AmneziaGeo.Linux.App [options]

          --iface <name>    tunnel interface to create (default: awg0)
          --engine <path>   amneziawg-go binary (default: <app dir>/amneziawg-go)
          --data <dir>      library root holding state.db, logs and geo bases
                            (default: $AMNEZIAGEO_DATA, else ~/.local/share/AmneziaGeo)
          --version         print the agent version and exit
          --help, -h        print this help and exit

        The agent needs root: it creates the tunnel device and rewrites routes.
        Configure it from the console with the amneziageo client, or from the desktop UI.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (!Options.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(_usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(_usage);
            return 0;
        }

        if (options.Version)
        {
            Console.WriteLine(AgentBuild.Version);
            return 0;
        }

        // Set before anything touches AgentPaths: its root is resolved once, on first access.
        if (options.DataRoot is { Length: > 0 } dataRoot)
        {
            Environment.SetEnvironmentVariable("AMNEZIAGEO_DATA", dataRoot);
        }

        using var log = new AgentLog(AgentPaths.LogDb);
        await log.InitializeAsync().ConfigureAwait(false);
        log.Info("agent", $"starting: pid {Environment.ProcessId}, version {AgentBuild.Version}, interface {options.Interface}, engine {options.EnginePath} (present: {File.Exists(options.EnginePath)})");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });

        using var agent = new LinuxAgent(log, options.EnginePath, options.Interface);
        using var server = new StatusPipeServer(agent, log);
        try
        {
            await agent.InitializeAsync(cts.Token).ConfigureAwait(false);
            log.Info("agent", "listening on pipe AmneziaGeo.Agent");
            var supervisor = agent.RunSupervisorAsync(cts.Token);
            await server.RunAsync(cts.Token).ConfigureAwait(false);
            await supervisor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Error("agent", "agent stopped on an unhandled error", ex);
            await log.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 1;
        }

        log.Info("agent", "stopped");
        await log.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Command line of the agent.
    /// </summary>
    private sealed record Options(string Interface, string EnginePath, string? DataRoot, bool Help, bool Version)
    {
        /// <summary>
        /// Parses the command line, rejecting anything unknown.
        /// </summary>
        public static bool TryParse(string[] args, out Options options, out string error)
        {
            var iface = default(string);
            var engine = default(string);
            var data = default(string);
            var help = false;
            var version = false;
            error = string.Empty;

            for (var i = 0; i < args.Length; i++)
            {
                var (flag, inline) = Split(args[i]);
                switch (flag)
                {
                    case "--help" or "-h":
                        help = true;
                        break;
                    case "--version":
                        version = true;
                        break;
                    case "--iface" or "--interface":
                        if (!TryValue(args, ref i, inline, out iface))
                        {
                            error = $"{flag} needs a value";
                            options = Empty();
                            return false;
                        }

                        break;
                    case "--engine":
                        if (!TryValue(args, ref i, inline, out engine))
                        {
                            error = $"{flag} needs a value";
                            options = Empty();
                            return false;
                        }

                        break;
                    case "--data" or "--root":
                        if (!TryValue(args, ref i, inline, out data))
                        {
                            error = $"{flag} needs a value";
                            options = Empty();
                            return false;
                        }

                        break;
                    default:
                        error = $"unknown argument '{args[i]}'";
                        options = Empty();
                        return false;
                }
            }

            options = new Options(
                iface ?? "awg0",
                engine ?? Path.Combine(AppContext.BaseDirectory, "amneziawg-go"),
                data,
                help,
                version);
            return true;
        }

        private static Options Empty() => new("awg0", string.Empty, null, false, false);

        // Accepts both "--flag value" and "--flag=value".
        private static (string Flag, string? Inline) Split(string argument)
        {
            var separator = argument.IndexOf('=');
            return separator > 0
                ? (argument[..separator], argument[(separator + 1)..])
                : (argument, null);
        }

        private static bool TryValue(string[] args, ref int index, string? inline, out string? value)
        {
            if (inline is { Length: > 0 })
            {
                value = inline;
                return true;
            }

            if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
            {
                value = args[++index];
                return true;
            }

            value = null;
            return false;
        }
    }
}
