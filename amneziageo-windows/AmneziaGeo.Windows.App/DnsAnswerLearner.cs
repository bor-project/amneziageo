using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AmneziaGeo.Routing;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Hands the name proxy the answers programs get past it, read from the DNS-Client ETW provider.
/// </summary>
internal sealed partial class DnsAnswerLearner(DnsProxy proxy, Func<IPAddress, bool> foreign, ILogger logger)
{
    // Microsoft-Windows-DNS-Client.
    private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    // Event 3008: a DNS query completed, in the caller's process context.
    private const int DnsQueryCompletedId = 3008;
    private const string SessionName = "AmneziaGeoDnsAnswers";
    // How soon the session hands over the events it holds.
    private const uint FlushTimerMs = 50;
    private const uint RealTimeMode = 0x100;
    private const uint MillisecondFlushTimer = 0x10;
    private const uint TracedGuid = 0x20000;
    private const uint QueryPerformanceClock = 1;
    private const uint ControlStop = 1;
    private const uint EnableProvider = 1;
    private const byte LevelInformational = 4;
    private const int AlreadyExists = 183;
    private const int NameChars = 256;
    private static readonly int OwnProcessId = Environment.ProcessId;

    /// <summary>
    /// Reads the completed lookups until cancellation.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var handle = StartSession();
            var stopped = 0;
            void Stop()
            {
                if (Interlocked.Exchange(ref stopped, 1) == 0)
                {
                    StopSession(handle);
                }
            }

            try
            {
                using var source = new ETWTraceEventSource(SessionName, TraceEventSourceType.Session);
                using var registration = ct.Register(Stop);
                source.Dynamic.All += Handle;
                await Task.Run(() => source.Process(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Stop();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "stopped reading the answers Windows reports for name lookups");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the answers Windows reports for name lookups could not be read, so rules by domain do not apply while lookups go past the name proxy");
        }
    }

    /// <summary>
    /// The IPv4 addresses in the results of a completed lookup, mapped ones included.
    /// </summary>
    internal static IReadOnlyList<IPAddress> ParseResults(string results)
    {
        var found = new List<IPAddress>();
        foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains(' ')
                || (!part.Contains(':') && part.Count(c => c == '.') != 3)
                || !IPAddress.TryParse(part, out var parsed))
            {
                continue;
            }

            var address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
            if (address.AddressFamily == AddressFamily.InterNetwork
                && !address.Equals(IPAddress.Any)
                && !IPAddress.IsLoopback(address)
                && !found.Contains(address))
            {
                found.Add(address);
            }
        }

        return found;
    }

    private void Handle(TraceEvent evt)
    {
        if ((int)evt.ID != DnsQueryCompletedId || evt.ProcessID == OwnProcessId)
        {
            return;
        }

        try
        {
            if (Convert.ToInt64(evt.PayloadByName("QueryStatus")) != 0
                || evt.PayloadByName("QueryName") is not string name
                || evt.PayloadByName("QueryResults") is not string results
                || ParseResults(results) is not { Count: > 0 } addresses)
            {
                return;
            }

            var verdict = proxy.Learn(name, addresses, foreign);
            if (verdict != RouteVerdict.None)
            {
                logger.LogDebug("{Name}: a program got {Count} address(es) for it past the name proxy, and the name rules settle them as {Verdict}", name, addresses.Count, verdict);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "an answer Windows reported for a name lookup could not be applied");
        }
    }

    // Starts the real-time session, replacing one left under the same name, with the DNS client enabled in it.
    private static ulong StartSession()
    {
        var handle = 0UL;
        var status = WithProperties(block => StartTrace(out handle, SessionName, block));
        if (status == AlreadyExists)
        {
            WithProperties(block => ControlTrace(0, SessionName, block, ControlStop));
            status = WithProperties(block => StartTrace(out handle, SessionName, block));
        }

        if (status != 0)
        {
            throw new Win32Exception(status);
        }

        var provider = DnsClientProvider;
        status = EnableTraceEx2(handle, ref provider, EnableProvider, LevelInformational, ulong.MaxValue, 0, 0, IntPtr.Zero);
        if (status != 0)
        {
            WithProperties(block => ControlTrace(handle, null, block, ControlStop));
            throw new Win32Exception(status);
        }

        return handle;
    }

    // Stops the session started under this handle.
    private void StopSession(ulong handle)
    {
        var status = WithProperties(block => ControlTrace(handle, null, block, ControlStop));
        if (status != 0)
        {
            logger.LogDebug(new Win32Exception(status), "the reading of the answers Windows reports did not stop cleanly");
        }
    }

    // Calls into ETW with the properties of this session.
    private static int WithProperties(Func<IntPtr, int> call)
    {
        var header = Marshal.SizeOf<TraceProperties>();
        var size = header + (NameChars * sizeof(char));
        var block = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, block, size);
            var properties = new TraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)size, ClientContext = QueryPerformanceClock, Flags = TracedGuid },
                LogFileMode = RealTimeMode | MillisecondFlushTimer,
                FlushTimer = FlushTimerMs,
                LoggerNameOffset = (uint)header,
            };
            Marshal.StructureToPtr(properties, block, false);
            return call(block);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StartTrace(out ulong traceHandle, string instanceName, IntPtr properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ControlTrace(ulong traceHandle, string? instanceName, IntPtr properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    private static partial int EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);
}
