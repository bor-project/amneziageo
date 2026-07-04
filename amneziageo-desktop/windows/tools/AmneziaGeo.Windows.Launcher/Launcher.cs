using AmneziaGeo.Windows.App;
using Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UiProgram = AmneziaGeo.Windows.Ui.Program;

namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Hosts the backend agent and the desktop UI in-process for debugging, replacing the scripts.
/// </summary>
internal sealed class Launcher(ILogger<Launcher> logger, IOptions<LauncherOptions> options)
{
    /// <summary>
    /// Launches the configured components in-process and blocks until they exit.
    /// </summary>
    public int Run(string[] args)
    {
        var launch = LaunchOptions.Resolve(options.Value, args);

        if (ShouldSeed())
        {
            RegisterConfigs(launch.ConfigPaths);
            ApplyRoutingLists(options.Value.RoutingLists);
            ApplyProfiles(options.Value.Profiles);
            ApplyWebSockets(options.Value.WebSockets);
            MarkSeeded();
        }

        if (!launch.RunService && !launch.RunUi)
        {
            logger.LogWarning("nothing to launch");
            return 0;
        }

        using (var cts = new CancellationTokenSource())
        {
            Task<int>? agentTask = null;

            if (launch.RunService)
            {
                var target = launch.Target ?? "main";

                if (launch.ConfigPath is not null)
                {
                    logger.LogInformation("registering config '{Target}' from {Path}", target, launch.ConfigPath);
                    AppEntry.RunAsync(["config-add", target, launch.ConfigPath]).GetAwaiter().GetResult();
                }

                logger.LogInformation("starting backend (agent, in-process) for '{Target}'", target);
                agentTask = Task.Run(() => AppEntry.RunAsync(["--agent", target], cts.Token));
            }

            if (launch.RunUi)
            {
                ScheduledTaskInstaller.EnsureLogonTask(logger);

                logger.LogInformation("starting UI");
                UiProgram.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                cts.Cancel();
            }

            if (agentTask is not null)
            {
                try
                {
                    agentTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        return 0;
    }

    private void ApplyRoutingLists(IReadOnlyList<RoutingListSpec> lists)
    {
        foreach (var list in lists)
        {
            if (string.IsNullOrWhiteSpace(list.Name))
            {
                continue;
            }

            logger.LogInformation("ensuring routing list '{Name}' ({Count} rule(s))", list.Name, list.Rules.Length);
            try
            {
                var args = new List<string> { "routing-list-add", list.Name };
                args.AddRange(list.Rules);
                AppEntry.RunAsync([.. args]).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "failed to apply routing list '{Name}'", list.Name);
            }
        }
    }

    private void ApplyProfiles(IReadOnlyList<ProfileSpec> profiles)
    {
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Members.Length == 0)
            {
                continue;
            }

            logger.LogInformation("ensuring profile '{Name}' ({Count} member(s))", profile.Name, profile.Members.Length);
            try
            {
                var addArgs = new List<string>
                {
                    "balancer-add",
                    profile.Name,
                    profile.RecheckSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                addArgs.AddRange(profile.Members);
                AppEntry.RunAsync([.. addArgs]).GetAwaiter().GetResult();

                if (!string.IsNullOrWhiteSpace(profile.Mode))
                {
                    AppEntry.RunAsync(["balancer-mode", profile.Name, profile.Mode]).GetAwaiter().GetResult();
                }

                var listName = string.IsNullOrWhiteSpace(profile.RoutingList) ? "none" : profile.RoutingList;
                AppEntry.RunAsync(["assign-routing", profile.Name, listName, profile.UseRouting ? "on" : "off"]).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "failed to apply profile '{Name}'", profile.Name);
            }
        }
    }

    private bool ShouldSeed()
    {
        if (!options.Value.SeedOnce)
        {
            return true;
        }

        if (File.Exists(SeedMarkerPath()))
        {
            logger.LogInformation("seed-once: marker present, skipping startup seed");
            return false;
        }

        return true;
    }

    private void MarkSeeded()
    {
        if (!options.Value.SeedOnce)
        {
            return;
        }

        try
        {
            var marker = SeedMarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "seeded");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to write seed-once marker");
        }
    }

    private static string SeedMarkerPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AmneziaGeo",
        "launcher-seeded.marker");

    private void ApplyWebSockets(IReadOnlyList<WebSocketSpec> webSockets)
    {
        foreach (var ws in webSockets)
        {
            if (string.IsNullOrWhiteSpace(ws.Config))
            {
                continue;
            }

            logger.LogInformation("seeding WebSocket transport for '{Config}' (enabled={Enabled}, port={Port})", ws.Config, ws.Enabled, ws.Port);
            try
            {
                AppEntry.RunAsync(
                [
                    "set-websocket",
                    ws.Config,
                    ws.Enabled ? "on" : "off",
                    ws.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ws.Host ?? string.Empty,
                ]).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "failed to seed WebSocket transport for '{Config}'", ws.Config);
            }
        }
    }

    private void RegisterConfigs(IReadOnlyList<string> paths)
    {
        foreach (var raw in paths)
        {
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            if (!Path.IsPathRooted(expanded))
            {
                expanded = Path.GetFullPath(expanded, AppContext.BaseDirectory);
            }

            string[] files;
            if (Directory.Exists(expanded))
            {
                files = Directory.GetFiles(expanded, "*.conf");
            }
            else if (File.Exists(expanded))
            {
                files = [expanded];
            }
            else
            {
                logger.LogWarning("config path not found, skipping: {Path}", expanded);
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                logger.LogInformation("registering config '{Name}' from {Path}", name, file);
                try
                {
                    AppEntry.RunAsync(["config-add", name, file]).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "failed to register config '{Name}'", name);
                }
            }
        }
    }
}
