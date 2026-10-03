using System.Diagnostics;

using AmneziaGeo.Cli;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// The systemd unit that runs the agent on a server.
/// </summary>
internal static class Systemd
{
    /// <summary>
    /// Unit name.
    /// </summary>
    public const string Unit = "amneziageo-agent.service";

    /// <summary>
    /// Where 'daemon install' writes the unit file.
    /// </summary>
    public const string UnitPath = "/etc/systemd/system/" + Unit;

    /// <summary>
    /// Where the package ships the unit file.
    /// </summary>
    public const string PackagedUnitPath = "/usr/lib/systemd/system/" + Unit;

    /// <summary>
    /// Default library root of a service install.
    /// </summary>
    public const string DefaultDataRoot = "/var/lib/amneziageo";

    /// <summary>
    /// Default install directory of a service install.
    /// </summary>
    public const string DefaultPrefix = "/opt/amneziageo";

    /// <summary>
    /// Path the installed unit file sits at, or null.
    /// </summary>
    public static string? InstalledPath =>
        File.Exists(UnitPath) ? UnitPath
        : File.Exists(PackagedUnitPath) ? PackagedUnitPath
        : null;

    /// <summary>
    /// Whether the unit file is installed.
    /// </summary>
    public static bool Exists => InstalledPath is not null;

    /// <summary>
    /// Short state of the unit.
    /// </summary>
    public static string State()
    {
        var active = Run("systemctl", ["is-active", Unit]);
        var enabled = Run("systemctl", ["is-enabled", Unit]);
        return $"{active.Output.Trim()}, {enabled.Output.Trim()} at boot";
    }

    /// <summary>
    /// Runs a process and captures its output.
    /// </summary>
    public static (int Code, string Output) Run(string file, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(file)
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
                return (127, $"could not start {file}");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (127, $"{file}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the unit file text.
    /// </summary>
    public static string UnitText(string agentPath, string dataRoot, string iface, string? enginePath)
    {
        var engine = enginePath is { Length: > 0 } ? $" --engine {enginePath}" : string.Empty;
        return $"""
            [Unit]
            Description=AmneziaGeo agent
            Documentation=https://github.com/amneziageo
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=simple
            ExecStart={agentPath} --iface {iface}{engine}
            Environment=AMNEZIAGEO_DATA={dataRoot}
            # The control socket is /tmp/CoreFxPipe_AmneziaGeo.Agent: a private /tmp hides it from every client.
            PrivateTmp=no
            # The agent creates the tunnel device and rewrites routes.
            User=root
            RuntimeDirectory=amneziawg
            Restart=on-failure
            RestartSec=5
            KillSignal=SIGTERM
            TimeoutStopSec=20

            [Install]
            WantedBy=multi-user.target

            """;
    }
}

/// <summary>
/// Service lifecycle commands.
/// </summary>
internal static class DaemonCommands
{
    private const string _bootFollowsFlag = "start at boot follows survive-reboot: amneziageo settings set survive-reboot on|off";

    /// <summary>
    /// Runs one daemon command.
    /// </summary>
    public static async Task<int> RunAsync(ICliHost host, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo daemon <install|uninstall|start|stop|restart|status|logs>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(1)];
        return args[0] switch
        {
            "install" => await InstallAsync(rest).ConfigureAwait(false),
            "uninstall" => Uninstall(),
            "start" => await StartAsync(host, ct).ConfigureAwait(false),
            "stop" => Stop(),
            "restart" => Control(args[0]),
            "enable" or "disable" => Reply.Usage(_bootFollowsFlag),
            "status" => Status(),
            "logs" => Logs(rest),
            _ => Reply.Usage($"unknown daemon command '{args[0]}'"),
        };
    }

    private static async Task<int> InstallAsync(IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "print");
        if (!flags.Allowed("data", "iface", "engine", "agent", "print"))
        {
            return Reply.Usage(flags.Error!);
        }

        var agentPath = flags.Value("agent")
            ?? Locate()
            ?? (flags.Has("print") ? Path.Combine(Systemd.DefaultPrefix, "AmneziaGeo.Linux.App") : null);
        if (agentPath is null)
        {
            return Reply.Usage("the agent binary was not found next to the client; pass --agent <path>");
        }

        var text = Systemd.UnitText(
            agentPath,
            flags.Value("data") ?? Systemd.DefaultDataRoot,
            flags.Value("iface") ?? "awg0",
            flags.Value("engine"));

        if (flags.Has("print"))
        {
            Output.Line(text);
            return Exit.Ok;
        }

        if (!IsRoot())
        {
            return Reply.Usage($"installing the unit needs root: sudo amneziageo daemon install");
        }

        if (File.Exists(Systemd.PackagedUnitPath))
        {
            Output.Info($"the package already ships {Systemd.PackagedUnitPath}; the unit written now overrides it");
        }

        await File.WriteAllTextAsync(Systemd.UnitPath, text).ConfigureAwait(false);
        Output.Info($"wrote {Systemd.UnitPath}");

        var reload = Systemd.Run("systemctl", ["daemon-reload"]);
        if (reload.Code != 0)
        {
            Output.Error(reload.Output);
            return Exit.Failed;
        }

        // The agent enables its unit at boot itself once survive-reboot is on.
        var start = Systemd.Run("systemctl", ["start", Systemd.Unit]);
        if (start.Code != 0)
        {
            Output.Error(start.Output.Trim());
            return Exit.Failed;
        }

        Output.Info($"the library lives in {flags.Value("data") ?? Systemd.DefaultDataRoot}; import a configuration with 'amneziageo config import'");
        Output.Info(_bootFollowsFlag);
        return Exit.Ok;
    }

    private static int Uninstall()
    {
        if (!File.Exists(Systemd.UnitPath) && File.Exists(Systemd.PackagedUnitPath))
        {
            return Reply.Usage("the unit comes from the amneziageo package: sudo apt remove amneziageo");
        }

        if (!IsRoot())
        {
            return Reply.Usage("removing the unit needs root: sudo amneziageo daemon uninstall");
        }

        Systemd.Run("systemctl", ["disable", "--now", Systemd.Unit]);
        if (File.Exists(Systemd.UnitPath))
        {
            File.Delete(Systemd.UnitPath);
            Output.Info($"removed {Systemd.UnitPath}");
        }

        Systemd.Run("systemctl", ["daemon-reload"]);
        Output.Info("the library was left in place");
        return Exit.Ok;
    }

    // Starts the unit and reads back what survive-reboot made of its start at boot.
    private static async Task<int> StartAsync(ICliHost host, CancellationToken ct)
    {
        if (!Systemd.Exists)
        {
            return Reply.Usage($"{Systemd.Unit} is not installed: sudo apt install amneziageo, or sudo amneziageo daemon install");
        }

        if (!IsRoot())
        {
            return Reply.Usage("starting the agent needs root: sudo amneziageo start");
        }

        var started = Systemd.Run("systemctl", ["start", Systemd.Unit]);
        if (started.Code != 0)
        {
            Output.Error(started.Output.Trim());
            Output.Error("the reason is in 'amneziageo daemon logs'");
            return Exit.Failed;
        }

        // The agent settles its start at boot before it opens the socket, so the first snapshot already holds it.
        var agent = await host.ConnectAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        try
        {
            if (agent is null)
            {
                Output.Error($"{Systemd.Unit} started but the agent does not answer; the reason is in 'amneziageo daemon logs'");
                return Exit.Failed;
            }

            var survive = agent.Snapshot.SurviveReboot;
            var atBoot = Systemd.Run("systemctl", ["is-enabled", Systemd.Unit]).Output.Trim();
            if (Output.Json)
            {
                Output.AsJson(new { unit = Systemd.Unit, active = true, atBoot, surviveReboot = survive });
                return Exit.Ok;
            }

            Output.Pairs(
            [
                ("agent", "running"),
                ("at boot", atBoot),
                ("survive reboot", survive ? "on" : "off"),
            ]);
            Output.Info(survive
                ? "the agent starts at boot and connects again; 'amneziageo settings set survive-reboot off' keeps it off after a reboot"
                : "the agent stays off after a reboot; 'amneziageo settings set survive-reboot on' brings it back connected");
            return Exit.Ok;
        }
        finally
        {
            (agent as IDisposable)?.Dispose();
        }
    }

    // Stops the unit; whether it comes back at boot stays with survive-reboot.
    private static int Stop()
    {
        if (!Systemd.Exists)
        {
            return Reply.Usage($"{Systemd.Unit} is not installed");
        }

        if (!IsRoot())
        {
            return Reply.Usage("stopping the agent needs root: sudo amneziageo stop");
        }

        if (!Output.Json)
        {
            Output.Info("this drops the tunnel: the agent tears the interface down when it exits");
        }

        var stopped = Systemd.Run("systemctl", ["stop", Systemd.Unit]);
        if (stopped.Code != 0)
        {
            Output.Error(stopped.Output.Trim());
            return Exit.Failed;
        }

        var atBoot = Systemd.Run("systemctl", ["is-enabled", Systemd.Unit]).Output.Trim();
        if (Output.Json)
        {
            Output.AsJson(new { unit = Systemd.Unit, active = false, atBoot });
            return Exit.Ok;
        }

        Output.Pairs([("agent", "stopped"), ("at boot", atBoot)]);
        Output.Info(atBoot == "enabled"
            ? "survive-reboot is on, so the agent starts again at boot; turn it off first to keep the agent off"
            : "the agent stays off until 'sudo amneziageo start'");
        return Exit.Ok;
    }

    private static int Control(string verb)
    {
        if (verb == "restart")
        {
            Output.Info("this drops the tunnel: the agent tears the interface down when it exits");
        }

        var result = Systemd.Run("systemctl", [verb, Systemd.Unit]);
        if (result.Output.Trim().Length > 0)
        {
            Output.Info(result.Output.Trim());
        }

        return result.Code == 0 ? Exit.Ok : Exit.Failed;
    }

    private static int Status()
    {
        var installed = Systemd.Exists;
        var socket = File.Exists(LinuxCliHost.SocketPath);
        var active = installed ? Systemd.Run("systemctl", ["is-active", Systemd.Unit]).Output.Trim() : "not installed";
        var enabled = installed ? Systemd.Run("systemctl", ["is-enabled", Systemd.Unit]).Output.Trim() : "-";

        if (Output.Json)
        {
            Output.AsJson(new { unit = Systemd.Unit, installed, active, enabled, socket });
            return installed && active == "active" && socket ? Exit.Ok : Exit.Failed;
        }

        Output.Pairs(
        [
            ("unit", Systemd.Unit),
            ("installed", Systemd.InstalledPath ?? "no"),
            ("active", active),
            ("at boot", enabled),
            ("control socket", socket ? LinuxCliHost.SocketPath : "missing"),
        ]);

        if (installed && active == "active" && !socket)
        {
            Output.Error("the unit runs but the control socket is missing: check that PrivateTmp is not set");
        }

        return installed && active == "active" && socket ? Exit.Ok : Exit.Failed;
    }

    private static int Logs(IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "follow");
        if (!flags.Allowed("follow", "since", "lines"))
        {
            return Reply.Usage(flags.Error!);
        }

        var arguments = new List<string> { "-u", Systemd.Unit, "-n", flags.Value("lines") ?? "200" };
        if (flags.Value("since") is { Length: > 0 } since)
        {
            arguments.Add("--since");
            arguments.Add(since);
        }

        if (flags.Has("follow"))
        {
            arguments.Add("-f");
        }

        var start = new ProcessStartInfo("journalctl");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            Output.Error("journalctl is not available");
            return Exit.Failed;
        }

        process.WaitForExit();
        return process.ExitCode == 0 ? Exit.Ok : Exit.Failed;
    }

    // The agent ships next to the client in a server install.
    private static string? Locate()
    {
        var directory = AppContext.BaseDirectory;
        foreach (var candidate in new[] { "AmneziaGeo.Linux.App", "amneziageo-agent" })
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        var dll = Path.Combine(directory, "AmneziaGeo.Linux.App.dll");
        return File.Exists(dll) ? $"/usr/bin/env dotnet {dll}" : null;
    }

    private static bool IsRoot() => Environment.UserName == "root" || Environment.GetEnvironmentVariable("USER") == "root";
}
