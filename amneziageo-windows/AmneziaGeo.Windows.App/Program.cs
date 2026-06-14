using AmneziaGeo.Decl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Windows host entry point.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var agentTarget = args is ["--agent", var target] ? target : null;
        using (var host = AppHost.Build(agentTarget))
        {
            await EnsureStoreAsync(host.Services);
            if (agentTarget is not null)
            {
                await host.RunAsync();
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
