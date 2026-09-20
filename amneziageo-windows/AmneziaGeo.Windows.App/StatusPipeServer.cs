using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Hosts the agent's status pipe: accepts UI clients and periodically pushes status snapshots.
/// </summary>
internal sealed class StatusPipeServer(AgentStatusBroker broker, AgentControl control, ILogger<StatusPipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan _pushInterval = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pushLoop = PushLoopAsync(stoppingToken);
        try
        {
            await AcceptLoopAsync(stoppingToken);
        }
        finally
        {
            await pushLoop;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex)
            {
                // Held pipe is expected and recoverable: back off without flooding the journal.
                var taken = ex is UnauthorizedAccessException or IOException;
                if (taken)
                {
                    logger.LogWarning("the channel the app talks over is still held by another copy ({Reason}); the window will not show a status until it is free, retrying every 5 s", ex.GetType().Name);
                }
                else
                {
                    logger.LogError(ex, "the channel the app talks over could not be opened; the window will show no status until this succeeds, retrying every second");
                }

                try
                {
                    await Task.Delay(taken ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "waiting for the app to connect failed; the channel is reopened, the window reconnects by itself");
                await pipe.DisposeAsync();
                continue;
            }

            _ = broker.HandleClientAsync(pipe, ct);
        }
    }

    private async Task PushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (control.Running)
                {
                    // Active session: poll so connect progress and liveness reach the UI promptly.
                    await Task.Delay(_pushInterval, ct);
                    await broker.BroadcastIfChangedAsync(ct);
                }
                else
                {
                    // Idle without a tunnel: wake on a status change instead of rebuilding the snapshot every 2s.
                    var wait = control.WaitForStatusAsync(ct);
                    await broker.BroadcastIfChangedAsync(ct);
                    await wait;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "the status could not be sent to the window; it will show the previous one until the next update");
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcContract.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }
}
