using System.Diagnostics;
using System.Text.Json;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Reports discarded UDP datagrams.
/// </summary>
public sealed record XHttpPressure(string Stage, ulong DroppedPackets, ulong DroppedBytes, ulong TotalPackets,
    ulong TotalBytes, ulong CapacityBytes, ulong CapacityPackets)
{
    private const string Marker = "AG_UDP_PRESSURE ";
    private static long _invalidWarningAt;

    internal static bool TryRead(string line, out XHttpPressure? pressure)
    {
        pressure = null;
        if (line.Length > 1024 || line.IndexOfAny(['\r', '\n']) >= 0)
        {
            return false;
        }

        var marker = line.IndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0 || marker > 256 || line.IndexOf(Marker, marker + Marker.Length, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var prefix = line[..marker];
        if (prefix.Length > 0 && (!prefix.Contains("[Warning]", StringComparison.Ordinal)
            || !prefix.EndsWith("app/proxyman/inbound: ", StringComparison.Ordinal)))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line[(marker + Marker.Length)..], new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var known = new HashSet<string>(["v", "stage", "droppedPackets", "droppedBytes", "totalPackets", "totalBytes", "capacityBytes", "capacityPackets"], StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
            {
                if (!known.Remove(field.Name))
                {
                    return false;
                }
            }

            if (known.Count != 0 || !Number(root, "v", out var version) || version != 1
                || root.GetProperty("stage").ValueKind != JsonValueKind.String
                || !Number(root, "droppedPackets", out var packets) || packets == 0
                || !Number(root, "droppedBytes", out var bytes)
                || !Number(root, "totalPackets", out var totalPackets) || totalPackets < packets
                || !Number(root, "totalBytes", out var totalBytes) || totalBytes < bytes
                || !Number(root, "capacityBytes", out var capacityBytes)
                || !Number(root, "capacityPackets", out var capacityPackets))
            {
                return false;
            }

            var stage = root.GetProperty("stage").GetString();
            if (stage == "pipe" && capacityBytes == 262144 && capacityPackets == 0
                || stage == "hub" && capacityBytes == 0 && capacityPackets == 256)
            {
                pressure = new XHttpPressure(stage, packets, bytes, totalPackets, totalBytes, capacityBytes, capacityPackets);
                return true;
            }

            return false;
        }
        catch (JsonException ex)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _invalidWarningAt);
            if ((previous == 0 || now - previous >= Stopwatch.Frequency * 10)
                && Interlocked.CompareExchange(ref _invalidWarningAt, now, previous) == previous)
            {
                Trace.TraceWarning("The XHTTP UDP pressure event was invalid: {0}", ex);
            }

            return false;
        }
    }

    internal static void Forward(string line, Action<XHttpPressure>? notify, Action<string, Exception?>? note, bool stopping)
    {
        if (stopping || notify is null || !TryRead(line, out var pressure))
        {
            return;
        }

        try
        {
            notify(pressure!);
        }
        catch (Exception ex)
        {
            try
            {
                note?.Invoke("The XHTTP UDP pressure warning could not be reported.", ex);
            }
            catch (Exception reportingError)
            {
                Trace.TraceError("The XHTTP UDP pressure warning callback failed: {0}; reporting failed: {1}", ex, reportingError);
            }
        }
    }

    private static bool Number(JsonElement root, string name, out ulong value)
    {
        value = 0;
        var field = root.GetProperty(name);
        return field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out value);
    }
}
