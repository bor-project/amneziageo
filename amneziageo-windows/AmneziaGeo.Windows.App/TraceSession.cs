using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A real-time ETW session of one provider on a single pool of small buffers: a flush hands what was logged to the
/// reader at once, where the buffers of every processor reach it on the next second.
/// </summary>
internal sealed partial class TraceSession : IDisposable
{
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint ClientContextQpc = 1;
    private const uint RealTimeMode = 0x00000100;
    private const uint NoPerProcessorBuffering = 0x10000000;
    private const uint ControlStop = 1;
    private const uint ControlFlush = 3;
    private const uint EnableProvider = 1;
    private const byte LevelInformational = 4;
    private const int ErrorAlreadyExists = 183;
    // EVENT_TRACE_PROPERTIES with the room for the session name behind it.
    private const int PropertiesSize = 120 + 2048;
    private const int LoggerNameOffset = 120;
    private const int BufferKb = 8;
    private const int MinimumBuffers = 4;
    private const int MaximumBuffers = 64;
    private const int FlushSeconds = 1;

    private readonly ulong _handle;
    private int _stopped;

    private TraceSession(ulong handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Starts a session under the name, in place of one of that name left behind, with the provider enabled on it.
    /// </summary>
    public static TraceSession Start(string name, Guid provider)
    {
        var rc = StartNamed(name, out var handle);
        if (rc == ErrorAlreadyExists)
        {
            Control(0, name, ControlStop);
            rc = StartNamed(name, out handle);
        }

        if (rc != 0)
        {
            throw new Win32Exception((int)rc, $"the session {name} could not be started");
        }

        var session = new TraceSession(handle);
        rc = EnableTraceEx2(handle, ref provider, EnableProvider, LevelInformational, ulong.MaxValue, 0, 0, IntPtr.Zero);
        if (rc != 0)
        {
            session.Dispose();
            throw new Win32Exception((int)rc, $"the provider could not be enabled on the session {name}");
        }

        return session;
    }

    /// <summary>
    /// Hands what was logged so far to the reader.
    /// </summary>
    public void Flush()
    {
        if (Volatile.Read(ref _stopped) == 0)
        {
            Control(_handle, null, ControlFlush);
        }
    }

    /// <summary>
    /// Stops the session, which ends the reader's processing.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            Control(_handle, null, ControlStop);
        }
    }

    /// <summary>
    /// Деструктор
    /// </summary>
    public void Dispose() => Stop();

    private static uint StartNamed(string name, out ulong handle)
    {
        var properties = Properties();
        try
        {
            return StartTraceW(out handle, name, properties);
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    private static uint Control(ulong handle, string? name, uint code)
    {
        var properties = Properties();
        try
        {
            return ControlTraceW(handle, name, properties, code);
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    // EVENT_TRACE_PROPERTIES of the session: real time, one pool of small buffers, the timer at its floor.
    private static IntPtr Properties()
    {
        var properties = Marshal.AllocHGlobal(PropertiesSize);
        for (var at = 0; at < PropertiesSize; at += sizeof(int))
        {
            Marshal.WriteInt32(properties, at, 0);
        }

        Marshal.WriteInt32(properties, 0, PropertiesSize);
        Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, properties + 24, 16);
        Marshal.WriteInt32(properties, 40, (int)ClientContextQpc);
        Marshal.WriteInt32(properties, 44, (int)WnodeFlagTracedGuid);
        Marshal.WriteInt32(properties, 48, BufferKb);
        Marshal.WriteInt32(properties, 52, MinimumBuffers);
        Marshal.WriteInt32(properties, 56, MaximumBuffers);
        Marshal.WriteInt32(properties, 64, (int)(RealTimeMode | NoPerProcessorBuffering));
        Marshal.WriteInt32(properties, 68, FlushSeconds);
        Marshal.WriteInt32(properties, 116, LoggerNameOffset);
        return properties;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTraceW(out ulong handle, string name, IntPtr properties);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
}
