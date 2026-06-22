using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// A tiny client for the AmneziaGeo agent's status/control pipe — just enough to send one command and
/// read its ack. The installer (net8) can't reference the app's net10 IPC library, so the minimal
/// newline-delimited JSON protocol (camelCase, one envelope per line) is reproduced here. The agent
/// runs as LocalSystem and does the privileged geo download the unprivileged bootstrapper cannot.
/// </summary>
internal static class AgentPipeClient
{
    private const string PipeName = "AmneziaGeo.Agent";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The agent's reply to a command, plus what its status snapshots revealed while we waited.
    /// <paramref name="GeoUpdatesAvailable"/> is the number of geo sources flagged "update available" in the
    /// last snapshot seen (-1 if none seen) — used to decide whether a download is worth offering.</summary>
    public readonly record struct AgentReply(bool Ok, string Message, int GeoUpdatesAvailable);

    /// <summary>
    /// Connects (retrying until <paramref name="connectTimeout"/>), sends the command, and returns the
    /// agent's ack. While waiting for the ack it reads the status snapshots the agent pushes: it reports an
    /// aggregate download percent (across in-flight geo sources) to <paramref name="progress"/>, and tracks
    /// how many sources have an update available. Throws on connect/timeout/pipe failure.
    /// </summary>
    public static async Task<AgentReply> SendAsync(
        string op, string[] args, TimeSpan connectTimeout, TimeSpan ackTimeout, CancellationToken ct,
        IProgress<int>? progress = null)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct);

        var command = new Envelope { Type = "command", Command = new Command { Op = op, Args = args } };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, Json) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ackCts.CancelAfter(ackTimeout);

        var updatesAvailable = -1;

        // The agent pushes a status snapshot on connect and as state changes; read each one for progress /
        // update-availability, and return once our command's ack arrives.
        while (true)
        {
            var line = await reader.ReadLineAsync(ackCts.Token);
            if (line is null)
            {
                throw new IOException("agent closed the pipe before acking");
            }

            if (line.Length == 0)
            {
                continue;
            }

            var envelope = JsonSerializer.Deserialize<Envelope>(line, Json);
            if (envelope?.Type == "snapshot" && envelope.Snapshot?.Sources is { Length: > 0 } sources)
            {
                var inflight = sources.Where(s => s.Updating).ToArray();
                if (inflight.Length > 0 && progress is not null)
                {
                    progress.Report((int)inflight.Average(s => Math.Clamp(s.Progress, 0, 100)));
                }

                updatesAvailable = sources.Count(s => s.UpdateAvailable);
            }
            else if (envelope?.Type == "ack" && envelope.Ack is not null)
            {
                return new AgentReply(envelope.Ack.Ok, envelope.Ack.Message ?? string.Empty, updatesAvailable);
            }
        }
    }

    private sealed class Envelope
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("command")] public Command? Command { get; set; }
        [JsonPropertyName("ack")] public Ack? Ack { get; set; }
        [JsonPropertyName("snapshot")] public Snapshot? Snapshot { get; set; }
    }

    private sealed class Command
    {
        [JsonPropertyName("op")] public string Op { get; set; } = string.Empty;
        [JsonPropertyName("args")] public string[] Args { get; set; } = [];
    }

    private sealed class Ack
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class Snapshot
    {
        [JsonPropertyName("sources")] public Source[]? Sources { get; set; }
    }

    private sealed class Source
    {
        [JsonPropertyName("updating")] public bool Updating { get; set; }
        [JsonPropertyName("progress")] public int Progress { get; set; }
        [JsonPropertyName("updateAvailable")] public bool UpdateAvailable { get; set; }
    }
}
