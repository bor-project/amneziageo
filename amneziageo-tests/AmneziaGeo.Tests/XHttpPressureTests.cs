using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class XHttpPressureTests
{
    private static string Wire(string stage = "pipe") => "AG_UDP_PRESSURE " + JsonSerializer.Serialize(new
    {
        v = 1,
        stage,
        droppedPackets = 3UL,
        droppedBytes = 600UL,
        totalPackets = 8UL,
        totalBytes = 1600UL,
        capacityBytes = stage == "pipe" ? 262144UL : 0,
        capacityPackets = stage == "hub" ? 256UL : 0
    });

    [Theory]
    [InlineData("pipe")]
    [InlineData("hub")]
    public void ReadsOnlyNumericPressure(string stage)
    {
        Assert.True(XHttpPressure.TryRead(Wire(stage), out var value));
        Assert.Equal(stage, value!.Stage);
        Assert.Equal(3UL, value.DroppedPackets);
        Assert.Equal(1600UL, value.TotalBytes);
    }

    [Fact]
    public void ReadsPinnedWarningConsoleEnvelope()
    {
        Assert.True(XHttpPressure.TryRead("2026/11/06 17:00:00.000001 [Warning] app/proxyman/inbound: " + Wire(), out _));
    }

    public static IEnumerable<object[]> RejectedLines()
    {
        var line = Wire();
        yield return [""];
        yield return ["Xray 26.10.31 started"];
        yield return ["[Info] app/proxyman/inbound: " + line];
        yield return ["[Warning] proxy/vless/outbound: " + line];
        yield return [new string(' ', 1025) + line];
        yield return [line + "\r"];
        yield return [line + "\n"];
        yield return [line + " AG_UDP_PRESSURE {}"];
        yield return [line.Replace("\"v\":1", "\"v\":2")];
        yield return [line.Replace("\"stage\":\"pipe\"", "\"stage\":\"other\"")];
        yield return [line.Replace("\"droppedPackets\":3", "\"droppedPackets\":0")];
        yield return [line.Replace("\"totalPackets\":8", "\"totalPackets\":1")];
        yield return [line.Replace("\"totalBytes\":1600", "\"totalBytes\":1")];
        yield return [line.Replace("\"capacityBytes\":262144", "\"capacityBytes\":16384")];
        yield return [line.Replace("\"capacityPackets\":0", "\"capacityPackets\":256")];
        yield return [line[..^1] + ",\"target\":\"secret\"}"];
        yield return [line[..^1] + ",\"v\":1}"];
        yield return [line.Replace("\"droppedBytes\":600,", "")];
        foreach (var numeric in new[] { "null", "\"600\"", "{}", "[]", "true", "-1", "1.5", "18446744073709551616" })
        {
            yield return [line.Replace("\"droppedBytes\":600", "\"droppedBytes\":" + numeric)];
        }
    }

    [Theory]
    [MemberData(nameof(RejectedLines))]
    public void DiscardsUnrecognizedLines(string line)
    {
        Assert.False(XHttpPressure.TryRead(line, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void RetiredOrMissingConsumerReceivesNothing()
    {
        var calls = 0;
        XHttpPressure.Forward(Wire(), _ => calls++, null, true);
        XHttpPressure.Forward(Wire(), null, null, false);
        XHttpPressure.Forward("ordinary warning with private data", _ => calls++, null, false);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void CallbackFailureKeepsExceptionAndDoesNotForwardRawText()
    {
        var expected = new InvalidOperationException("synthetic callback");
        var reported = default(Exception);
        var message = "";
        XHttpPressure.Forward(Wire(), _ => throw expected, (text, error) =>
        {
            message = text;
            reported = error;
        }, false);
        Assert.Same(expected, reported);
        Assert.DoesNotContain("AG_UDP_PRESSURE", message);
    }

    [Fact]
    public void BrokenWarningAndNoteConsumersDoNotStopDrain()
    {
        var error = Record.Exception(() => XHttpPressure.Forward(Wire(), _ => throw new InvalidOperationException("warning"),
            (_, _) => throw new InvalidOperationException("note"), false));
        Assert.Null(error);
    }

    [Fact]
    public void InvalidJsonReportsExceptionObjectOnceWithoutRawLine()
    {
        using var listener = new PressureTraceListener();
        var gate = typeof(XHttpPressure).GetField("_invalidWarningAt", BindingFlags.Static | BindingFlags.NonPublic)!;
        gate.SetValue(null, 0L);
        Trace.Listeners.Add(listener);
        try
        {
            var raw = "AG_UDP_PRESSURE {";
            Assert.False(XHttpPressure.TryRead(raw, out _));
            Assert.False(XHttpPressure.TryRead(raw, out _));
            Assert.Equal(1, listener.Calls);
            Assert.True(listener.Error is JsonException);
            Assert.DoesNotContain(raw, listener.Format);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            gate.SetValue(null, 0L);
        }
    }

    private sealed class PressureTraceListener : TraceListener
    {
        public int Calls { get; private set; }
        public Exception? Error { get; private set; }
        public string Format { get; private set; } = "";
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
            string? format, params object?[]? args)
        {
            if (args?.FirstOrDefault() is JsonException error)
            {
                Calls++;
                Error = error;
                Format = format ?? "";
            }
        }
    }
}
