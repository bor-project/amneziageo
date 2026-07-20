using AmneziaGeo.Dal;
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
            // Skip the one-time legacy-config migration on the tunnel connect hot path (--service): the agent /
            // installer already ran it, and a tunnel only reads its config from the DB.
            await EnsureStoreAsync(host.Services, runMigration: args is not ["--service", _]);
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

    private static async Task EnsureStoreAsync(IServiceProvider services, bool runMigration)
    {
        if (runMigration)
        {
            // Before the DB is opened: pull a legacy ProgramData store into the per-user root on first run.
            DataMigration.SeedFromProgramData(services.GetRequiredService<ILoggerFactory>().CreateLogger("DataMigration"));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TunnelPaths.StateDbFile())!);
        var store = services.GetRequiredService<IStateStore>();
        await store.InitializeAsync();

        // Open the structured log store and bind the static routing-log writer to it.
        var logStore = services.GetRequiredService<SqliteLogStore>();
        await logStore.InitializeAsync();
        RouteLog.UseStore(logStore);

        if (runMigration)
        {
            // One-time: pull legacy on-disk wg-quick files into the database.
            await services.GetRequiredService<ConfigRepository>().MigrateLegacyConfigsAsync();
        }
    }
}
