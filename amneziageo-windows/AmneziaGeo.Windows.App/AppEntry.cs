using AmneziaGeo.Decl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Hosts the application: builds the DI host and runs either the agent or a CLI command.
/// </summary>
public static class AppEntry
{
    /// <summary>
    /// Runs the application for the given arguments, honoring the token so an in-process host can stop it.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // "--agent <name>" binds a specific launch target (the preconfigured installer seeds e.g. "main");
        // bare "--agent" runs the agent with no launch target so a clean install idles without a phantom
        // binding and instead picks up the persisted user selection (AgentControl.SelectedTargetKey).
        var agentTarget = args switch
        {
            ["--agent", var target] => target,
            ["--agent"] => string.Empty,
            _ => null,
        };
        using (var host = AppHost.Build(agentTarget))
        {
            await EnsureStoreAsync(host.Services);
            if (agentTarget is not null)
            {
                // Take over the status pipe from any prior owner (a crashed dev launcher, a stray dotnet
                // run child, or the installed service running next to a dev session) before the host binds
                // it; otherwise the pipe's DACL makes pipe creation spin forever on ACCESS_DENIED.
                var serviceManager = host.Services.GetRequiredService<ServiceManager>();
                var guardLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SoleAgentGuard");
                await SoleAgentGuard.EnsureSoleAsync(serviceManager, guardLogger, cancellationToken);

                await host.RunAsync(cancellationToken);
                return 0;
            }

            var cli = host.Services.GetRequiredService<Cli>();
            return await cli.RunAsync(args);
        }
    }

    private static async Task EnsureStoreAsync(IServiceProvider services)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TunnelPaths.StateDbFile())!);
        await services.GetRequiredService<IStateStore>().InitializeAsync();

        // One-time: pull any legacy on-disk wg-quick files (Configurations\*.conf, from before configs
        // lived in the database) into the database. Idempotent and cheap once everything is migrated.
        await services.GetRequiredService<ConfigRepository>().MigrateLegacyConfigsAsync();
    }
}
