using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Client for the agent pipe used by the installer.
/// </summary>
internal static class AgentPipeClient
{
    private const string PipeName = "AmneziaGeo.Agent";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Agent reply with status snapshot summary.
    /// </summary>
    public readonly record struct AgentReply(bool Ok, string Message, int GeoUpdatesAvailable);

    /// <summary>
    /// Connects, sends a command, returns the agent ack.
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
                return new AgentReply(envelope.Ack.Ok, Decode(envelope.Ack.Message), updatesAvailable);
            }
        }
    }

    /// <summary>
    /// Connects to the running agent and reports whether it has a selected (or bound) profile with a
    /// configuration - i.e. a connection that can be dialed straight after install (#188). Best-effort:
    /// returns false if the agent is not running or the snapshot does not arrive in time.
    /// </summary>
    public static async Task<bool> HasConnectableProfileAsync(
        TimeSpan connectTimeout, TimeSpan readTimeout, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct);

            var command = new Envelope { Type = "command", Command = new Command { Op = "attach-ui", Args = [] } };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, Json) + "\n");
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(readTimeout);

            while (true)
            {
                var line = await reader.ReadLineAsync(readCts.Token);
                if (line is null)
                {
                    return false;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var envelope = JsonSerializer.Deserialize<Envelope>(line, Json);
                if (envelope?.Type == "snapshot" && envelope.Snapshot is { } snapshot)
                {
                    return IsConnectable(snapshot);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    // A selected (or bound) target that resolves to a profile carrying a configuration is a dialable connection.
    private static bool IsConnectable(Snapshot snapshot)
    {
        var target = string.IsNullOrEmpty(snapshot.SelectedTarget) ? snapshot.BoundTarget : snapshot.SelectedTarget;
        if (string.IsNullOrEmpty(target) || snapshot.Profiles is null)
        {
            return false;
        }

        return snapshot.Profiles.Any(p =>
            !string.IsNullOrEmpty(p.Config)
            && (string.Equals(p.Name, target, StringComparison.Ordinal)
                || string.Equals(p.Config, target, StringComparison.Ordinal)));
    }

    // Agent acks may carry a localization key (IpcMessage encoding: marker char + key + unit-separated args)
    // instead of literal text. Resolve it through the shared strings so the installer shows real wording, not
    // a marker-prefixed key. A plain message (no marker) is returned unchanged.
    private const char MessageMarker = (char)1;
    private const char MessageSeparator = (char)31;

    private static string Decode(string? message)
    {
        if (string.IsNullOrEmpty(message) || message[0] != MessageMarker)
        {
            return message ?? string.Empty;
        }

        var parts = message[1..].Split(MessageSeparator);
        var args = parts.Length > 1 ? parts[1..] : [];
        return Loc.Instance.Get(parts[0], args);
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
        [JsonPropertyName("selectedTarget")] public string? SelectedTarget { get; set; }
        [JsonPropertyName("boundTarget")] public string? BoundTarget { get; set; }
        [JsonPropertyName("profiles")] public Profile[]? Profiles { get; set; }
    }

    private sealed class Source
    {
        [JsonPropertyName("updating")] public bool Updating { get; set; }
        [JsonPropertyName("progress")] public int Progress { get; set; }
        [JsonPropertyName("updateAvailable")] public bool UpdateAvailable { get; set; }
    }

    private sealed class Profile
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("config")] public string? Config { get; set; }
    }
}
