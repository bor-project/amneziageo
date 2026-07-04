using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Ensures a single agent owns the status pipe before the host binds it.
/// </summary>
internal static partial class SoleAgentGuard
{
    private static readonly TimeSpan TakeoverWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Frees the status pipe of any prior owner so the host can bind it. No-op when the pipe is already free.
    /// </summary>
    public static async Task EnsureSoleAsync(ServiceManager services, ILogger logger, CancellationToken ct)
    {
        if (!PipeHeld())
        {
            return;
        }

        if (WindowsServiceHelpers.IsWindowsService())
        {
            // Service yields to an interactive session rather than kill it.
            logger.LogWarning("status pipe already owned by another agent; service is yielding (no second backend)");
            return;
        }

        // Interactive/elevated start: stop the agent service cleanly (no failure-restart ping-pong).
        if (services.AgentState() == "RUNNING")
        {
            logger.LogInformation("status pipe owned by the AmneziaGeoAgent service; stopping it to take over");
            services.StopAgentQuiet();
            await WaitUntilFreeAsync(TakeoverWait, ct);
        }

        // Stray non-service owner: resolve from the pipe and kill its tree precisely.
        if (PipeHeld() && OwnerProcessId() is { } pid && pid != (uint)Environment.ProcessId)
        {
            TryKill(pid, logger);
            await WaitUntilFreeAsync(TakeoverWait, ct);
        }

        if (PipeHeld())
        {
            logger.LogWarning("status pipe still held after takeover; the host may fail to bind it (run elevated?)");
        }
    }

    private static bool PipeHeld()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", IpcContract.PipeName, PipeDirection.InOut);
            client.Connect(200);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return true; // exists but momentarily busy
        }
        catch
        {
            return false;
        }
    }

    private static uint? OwnerProcessId()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", IpcContract.PipeName, PipeDirection.InOut);
            client.Connect(500);
            return GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(uint pid, ILogger logger)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            logger.LogInformation("killing stale status-pipe owner {Process} (pid {Pid})", process.ProcessName, pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not kill stale status-pipe owner (pid {Pid})", pid);
        }
    }

    private static async Task WaitUntilFreeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!PipeHeld())
            {
                return;
            }

            try
            {
                await Task.Delay(250, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
