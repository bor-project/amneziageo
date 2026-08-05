using System.Diagnostics;
using System.Runtime.InteropServices;
using AmneziaGeo.Cli;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Console client of the AmneziaGeo agent.
/// </summary>
public static class Program
{
    private const int PrSetPtracer = 0x59616d61;

    private static async Task<int> Main(string[] args)
    {
        WaitForDebugger();

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        return await CliRunner.RunAsync(args, new LinuxCliHost(), stop.Token).ConfigureAwait(false);
    }

    private static void WaitForDebugger()
    {
        var requested = Environment.GetEnvironmentVariable("AMNEZIAGEO_WAIT_DEBUGGER");
        if (requested is not ("1" or "on" or "true"))
        {
            return;
        }

        // Yama allows ptrace only from a parent; the debugger starts beside us.
        _ = prctl(PrSetPtracer, nuint.MaxValue, 0, 0, 0);

        Console.Error.WriteLine($"waiting for a debugger, pid {Environment.ProcessId}");
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!Debugger.IsAttached && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }

        Console.Error.WriteLine(Debugger.IsAttached ? "debugger attached" : "no debugger came, running anyway");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}
