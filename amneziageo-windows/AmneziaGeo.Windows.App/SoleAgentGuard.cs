using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Guarantees a single agent owns the status pipe before the host starts. The status pipe's DACL grants
/// ordinary users only Read/Write — not the right to add a second server instance — so any leftover agent
/// (a crashed dev launcher, a stray "dotnet run" child, or the installed AmneziaGeoAgent service running
/// next to a dev session) makes every later start spin forever on ACCESS_DENIED while creating the pipe.
/// An interactive, elevated start (the dev launcher) takes over: it stops the agent service and/or kills
/// the stale pipe owner, then returns once the pipe is free. A start running AS the service is passive —
/// if another agent already holds the pipe (an interactive session) it yields instead of fighting the user.
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
            // We are the installed service and another process already owns the pipe — almost certainly an
            // interactive dev launcher. Yield to the user's session rather than kill it.
            logger.LogWarning("status pipe already owned by another agent; service is yielding (no second backend)");
            return;
        }

        // Interactive/elevated start: take over so a repeated launch always works.
        // The installed agent service is the most common owner — stop it cleanly (a clean stop does not
        // trigger Windows failure-restart, so there is no ping-pong).
        if (services.AgentState() == "RUNNING")
        {
            logger.LogInformation("status pipe owned by the AmneziaGeoAgent service; stopping it to take over");
            services.StopAgentQuiet();
            await WaitUntilFreeAsync(TakeoverWait, ct);
        }

        // A stray, non-service owner (a crashed dev launcher / dotnet run child) — find it via the pipe and
        // kill its tree. Resolving the owner from the pipe keeps this precise: we never blanket-kill dotnet.
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

    /// <summary>
    /// True when a live agent answers on the status pipe. Our server always keeps an instance waiting, so a
    /// connect timeout means the pipe is absent rather than merely busy.
    /// </summary>
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

    /// <summary>
    /// The process id of the current status-pipe server, obtained from a client handle, or null if it
    /// cannot be determined.
    /// </summary>
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
