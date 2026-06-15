using AmneziaGeo.Decl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        var agentTarget = args is ["--agent", var target] ? target : null;
        using (var host = AppHost.Build(agentTarget))
        {
            await EnsureStoreAsync(host.Services);
            if (agentTarget is not null)
            {
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
    }
}
