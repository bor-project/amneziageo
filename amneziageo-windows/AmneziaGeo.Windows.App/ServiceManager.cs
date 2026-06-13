using System.Diagnostics;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Installs and controls per-tunnel Windows services.
/// </summary>
internal static class ServiceManager
{
    /// <summary>
    /// Installs a tunnel service from a wg-quick config file.
    /// </summary>
    public static int Install(string name, string configPath)
    {
        var stored = TunnelPaths.ConfigFile(name);
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        File.Copy(configPath, stored, overwrite: true);

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
    /// Stops and removes a tunnel service.
    /// </summary>
    public static int Uninstall(string name)
    {
        Sc("stop", TunnelPaths.ServiceName(name));
        return Sc("delete", TunnelPaths.ServiceName(name));
    }

    /// <summary>
    /// Starts a tunnel service.
    /// </summary>
    public static int Start(string name)
    {
        return Sc("start", TunnelPaths.ServiceName(name));
    }

    /// <summary>
    /// Stops a tunnel service.
    /// </summary>
    public static int Stop(string name)
    {
        return Sc("stop", TunnelPaths.ServiceName(name));
    }

    /// <summary>
    /// Prints the status of a tunnel service.
    /// </summary>
    public static int Status(string name)
    {
        return Sc("query", TunnelPaths.ServiceName(name));
    }

    private static int Sc(params string[] arguments)
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

            Console.Write(output);
            if (error.Length > 0)
            {
                Console.Error.Write(error);
            }

            return process.ExitCode;
        }
    }
}
