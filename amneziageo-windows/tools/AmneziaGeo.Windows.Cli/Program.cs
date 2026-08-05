using System.Text;
using AmneziaGeo.Cli;

namespace AmneziaGeo.Windows.Cli;

/// <summary>
/// Console client of the AmneziaGeo agent.
/// </summary>
public static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        return await CliRunner.RunAsync(args, new WindowsCliHost(), stop.Token).ConfigureAwait(false);
    }
}
