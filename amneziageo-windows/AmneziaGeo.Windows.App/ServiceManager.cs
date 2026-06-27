using System.Diagnostics;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Installs and controls the AmneziaGeo Windows services (per-tunnel and the agent).
/// </summary>
internal sealed class ServiceManager
{
    /// <summary>
    /// Creates the tunnel service for an already-stored config, doing nothing if it already exists.
    /// </summary>
    public int CreateService(string name)
    {
        if (Exists(name))
        {
            return 0;
        }

        var serviceName = TunnelPaths.ServiceName(name);
        var binPath = $"\"{Environment.ProcessPath}\" --service {name}";
        var created = Sc(
            "create",
            serviceName,
            "binPath=",
            binPath,
            "type=",
            "own",
            "start=",
            "demand",
            "obj=",
            "LocalSystem",
            "DisplayName=",
            $"AmneziaGeo Tunnel {name}");
        if (created != 0)
        {
            return created;
        }

        return Sc("sidtype", serviceName, "unrestricted");
    }

    /// <summary>
    /// Returns whether the tunnel service exists.
    /// </summary>
    public bool Exists(string name)
    {
        return QueryState(name) != "ABSENT";
    }

    /// <summary>
    /// Returns the service state as RUNNING, STOPPED, PENDING, or ABSENT.
    /// </summary>
    public string QueryState(string name)
    {
        return StateOf(TunnelPaths.ServiceName(name));
    }

    /// <summary>
    /// Returns the agent service state as RUNNING, STOPPED, PENDING, or ABSENT.
    /// </summary>
    public string AgentState()
    {
        return StateOf(TunnelPaths.AgentServiceName());
    }

    private static string StateOf(string serviceName)
    {
        var (code, output, _) = Run("query", serviceName);
        if (code == 1060)
        {
            return "ABSENT";
        }

        if (output.Contains("RUNNING", StringComparison.Ordinal))
        {
            return "RUNNING";
        }

        if (output.Contains("STOPPED", StringComparison.Ordinal))
        {
            return "STOPPED";
        }

        if (output.Contains("PENDING", StringComparison.Ordinal))
        {
            return "PENDING";
        }

        return "ABSENT";
    }

    /// <summary>
    /// Stops and removes a tunnel service.
    /// </summary>
    public int Uninstall(string name)
    {
        Sc("stop", TunnelPaths.ServiceName(name));
        return Sc("delete", TunnelPaths.ServiceName(name));
    }

    /// <summary>
    /// Removes a tunnel service without printing the service-control output.
    /// </summary>
    public int DeleteService(string name)
    {
        return Run("delete", TunnelPaths.ServiceName(name)).Code;
    }

    /// <summary>
    /// Starts a tunnel service.
    /// </summary>
    public int Start(string name)
    {
        return Sc("start", TunnelPaths.ServiceName(name));
    }

    /// <summary>
    /// Stops a tunnel service.
    /// </summary>
    public int Stop(string name)
    {
        return Sc("stop", TunnelPaths.ServiceName(name));
    }

    /// <summary>
    /// Starts a tunnel service without printing the service-control output.
    /// </summary>
    public int StartQuiet(string name)
    {
        return Run("start", TunnelPaths.ServiceName(name)).Code;
    }

    /// <summary>
    /// Stops a tunnel service without printing the service-control output.
    /// </summary>
    public int StopQuiet(string name)
    {
        return Run("stop", TunnelPaths.ServiceName(name)).Code;
    }

    /// <summary>
    /// Prints the status of a tunnel service.
    /// </summary>
    public int Status(string name)
    {
        return Sc("query", TunnelPaths.ServiceName(name));
    }

    /// <summary>
    /// Installs the always-on agent service bound to a balancer or single-config target.
    /// </summary>
    public int InstallAgent(string target)
    {
        var serviceName = TunnelPaths.AgentServiceName();
        var binPath = $"\"{Environment.ProcessPath}\" --agent {target}";
        return Sc(
            "create",
            serviceName,
            "binPath=",
            binPath,
            "type=",
            "own",
            "start=",
            "auto",
            "obj=",
            "LocalSystem",
            "DisplayName=",
            "AmneziaGeo Agent");
    }

    /// <summary>
    /// Stops and removes the agent service.
    /// </summary>
    public int UninstallAgent()
    {
        Sc("stop", TunnelPaths.AgentServiceName());
        return Sc("delete", TunnelPaths.AgentServiceName());
    }

    /// <summary>
    /// Starts the agent service.
    /// </summary>
    public int StartAgent()
    {
        return Sc("start", TunnelPaths.AgentServiceName());
    }

    /// <summary>
    /// Stops the agent service.
    /// </summary>
    public int StopAgent()
    {
        return Sc("stop", TunnelPaths.AgentServiceName());
    }

    /// <summary>
    /// Stops the agent service without printing the service-control output.
    /// </summary>
    public int StopAgentQuiet()
    {
        return Run("stop", TunnelPaths.AgentServiceName()).Code;
    }

    /// <summary>
    /// Prints the status of the agent service.
    /// </summary>
    public int AgentStatus()
    {
        return Sc("query", TunnelPaths.AgentServiceName());
    }

    private static int Sc(params string[] arguments)
    {
        var (code, output, error) = Run(arguments);
        Console.Write(output);
        if (error.Length > 0)
        {
            Console.Error.Write(error);
        }

        return code;
    }

    private static (int Code, string Output, string Error) Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(startInfo)!)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, error);
        }
    }
}
