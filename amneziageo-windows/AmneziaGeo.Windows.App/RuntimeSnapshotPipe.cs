using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The agent's line to a running tunnel. The tunnel runs in its own service process, so neither its caches nor a
/// rule change reach it in-process: state is read and changes are announced over this pipe.
/// </summary>
internal static class RuntimeSnapshotPipe
{
    private const int ConnectTimeoutMs = 1500;
    // More than one instance, so a caller never meets the gap between one connection closing and the next opening.
    private const int MaxInstances = 4;
    private const int Attempts = 2;
    private const int RetryDelayMs = 250;

    /// <summary>
    /// Asks for the live cache snapshot.
    /// </summary>
    public const string OpSnapshot = "snapshot";

    /// <summary>
    /// Announces that the routing rules changed and the cache must be re-decided.
    /// </summary>
    public const string OpRules = "rules";

    /// <summary>
    /// Asks only for the live counts, without serialising every held destination.
    /// </summary>
    public const string OpCounts = "counts";

    /// <summary>
    /// Announces that the route lifetime changed and applies to what is already held.
    /// </summary>
    public const string OpTtl = "ttl";

    /// <summary>
    /// Asks what the tunnel carries right now, as one session report.
    /// </summary>
    public const string OpSessions = "sessions";

    /// <summary>
    /// Asks the tunnel to measure one destination. Only the process holding the cache can put an address on a
    /// path and take it off again, so the whole run happens there.
    /// </summary>
    public const string OpProbe = "probe";

    /// <summary>
    /// Composes a probe request: the op, the target, the path and the speed service.
    /// </summary>
    public static string Probe(string target, string path, string uploadUrl)
    {
        return string.Join('\t', OpProbe, target, path, uploadUrl);
    }

    /// <summary>
    /// Pipe a tunnel's service process serves on.
    /// </summary>
    public static string Name(string tunnel)
    {
        return "ageo-runtime-" + tunnel.Replace('\\', '_').Replace('/', '_');
    }

    /// <summary>
    /// Serves one request per connection until cancelled.
    /// </summary>
    public static async Task ServeAsync(string tunnel, Func<string, CancellationToken, Task<string>> handler, ILogger logger, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = default(NamedPipeServerStream);
            try
            {
                server = new NamedPipeServerStream(Name(tunnel), PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                logger.LogDebug(ex, "runtime pipe: listening on {Tunnel} failed", tunnel);
                await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                continue;
            }

            // Answer off the accept loop, so the next instance is listening before this one has replied.
            _ = Task.Run(() => AnswerAsync(server, tunnel, handler, logger, ct), CancellationToken.None);
        }
    }

    private static async Task AnswerAsync(NamedPipeServerStream server, string tunnel, Func<string, CancellationToken, Task<string>> handler, ILogger logger, CancellationToken ct)
    {
        try
        {
            using (server)
            {
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                var op = (await reader.ReadLineAsync(ct).ConfigureAwait(false))?.Trim() ?? string.Empty;
                var payload = Encoding.UTF8.GetBytes(await handler(op, ct).ConfigureAwait(false));
                await server.WriteAsync(payload, ct).ConfigureAwait(false);
                await server.FlushAsync(ct).ConfigureAwait(false);
                server.WaitForPipeDrain();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "runtime pipe: answering {Tunnel} failed", tunnel);
        }
    }

    /// <summary>
    /// Sends one request to a tunnel's service process; null when it serves none.
    /// </summary>
    public static string? Send(string tunnel, string op, ILogger logger)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                var reply = Exchange(tunnel, op);
                if (reply.Length > 0)
                {
                    return reply;
                }

                logger.LogWarning("the running tunnel {Tunnel} returned nothing for '{Op}' (attempt {Attempt}); the app may show empty or stale routing data", tunnel, op, attempt);
            }
            catch (Exception ex) when (attempt < Attempts)
            {
                logger.LogDebug(ex, "'{Op}' did not reach the running tunnel {Tunnel}; retrying", op, tunnel);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "'{Op}' never reached the running tunnel {Tunnel}; the app shows what this process knows, which is not the live routing data", op, tunnel);
                return null;
            }
        }

        return null;
    }

    // Raw bytes, no StreamWriter: the answer ends when the server closes its side, and a writer disposed after that
    // flushes into a broken pipe and throws over an answer already in hand.
    private static string Exchange(string tunnel, string op)
    {
        using var client = new NamedPipeClientStream(".", Name(tunnel), PipeDirection.InOut);
        client.Connect(ConnectTimeoutMs);
        client.Write(Encoding.UTF8.GetBytes(op + "\n"));
        client.Flush();

        using var answer = new MemoryStream();
        client.CopyTo(answer);
        return Encoding.UTF8.GetString(answer.GetBuffer(), 0, (int)answer.Length);
    }
}
