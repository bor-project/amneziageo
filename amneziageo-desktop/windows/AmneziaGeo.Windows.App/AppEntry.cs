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
        // Installer maintenance verbs (invoked by the MSI custom actions as SYSTEM) run standalone, before the
        // DI host / DB init, so they never recreate or lock the very data they may be removing (#167).
        switch (args)
        {
            case ["--uninstall-cleanup"]:
                InstallerMaintenance.RemoveTransientServices();
                return 0;
            case ["--wipe-config"]:
                InstallerMaintenance.WipeRuntimeData();
                return 0;
        }

        // "--agent <name>" seeds a launch target; bare "--agent" idles with no target and picks up the persisted selection.
        var agentTarget = args switch
        {
            ["--agent", var target] => target,
            ["--agent"] => string.Empty,
            _ => null,
        };
        using (var host = AppHost.Build(agentTarget))
        {
            // Seed DB is applied only by the privileged agent so it lands with SYSTEM ownership.
            if (agentTarget is not null)
            {
                SeedImporter.TryApply(
                    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SeedImporter"));
            }

            await EnsureStoreAsync(host.Services);
            if (agentTarget is not null)
            {
                // Take over the status pipe before binding: a prior owner's DACL otherwise spins creation on ACCESS_DENIED.
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
        var store = services.GetRequiredService<IStateStore>();
        await store.InitializeAsync();

        // One-time: pull legacy on-disk wg-quick files into the database.
        await services.GetRequiredService<ConfigRepository>().MigrateLegacyConfigsAsync();

        // One-time: move per-config DNS/exclusions and global all-UDP onto each profile's routing list.
        await store.MigrateConfigSettingsToRoutingAsync();
    }
}
