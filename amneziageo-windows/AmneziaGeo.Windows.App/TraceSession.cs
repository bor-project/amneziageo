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
    // ENABLE_TRACE_PARAMETERS_VERSION_2, with filters.
    private const uint ParametersVersion = 2;
    // EVENT_FILTER_TYPE_EVENT_ID: the provider writes the listed events and no other.
    private const uint FilterTypeEventId = 0x80000200;
    // EVENT_FILTER_EVENT_ID of one event: FilterIn, Reserved, Count, Events[1].
    private const int EventFilterSize = 6;

    private readonly ulong _handle;
    // The properties block of the control calls, freed with the session.
    private IntPtr _properties;
    private readonly object _sync = new();
    private bool _stopped;

    /// <summary>
    /// ctor
    /// </summary>
    internal TraceSession(ulong handle)
    {
        _handle = handle;
        _properties = Properties();
    }

    /// <summary>
    /// Whether the provider was told to write the one event asked for and no other.
    /// </summary>
    public bool Filtered { get; private set; }

    /// <summary>
    /// Starts a session under the name, in place of one of that name left behind, with the provider enabled on it:
    /// for the one event when <paramref name="eventId"/> names it, else for every event.
    /// </summary>
    public static TraceSession Start(string name, Guid provider, ushort eventId = 0)
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
        rc = eventId == 0 ? EnableAll(handle, provider) : EnableOne(handle, provider, eventId);
        if (rc == 0)
        {
            session.Filtered = eventId != 0;
        }
        else if (eventId != 0)
        {
            // A system that takes no event filter still gets the provider whole.
            rc = EnableAll(handle, provider);
        }

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
        lock (_sync)
        {
            if (!_stopped)
            {
                Prepare(_properties);
                ControlTraceW(_handle, null, _properties, ControlFlush);
            }
        }
    }

    /// <summary>
    /// Stops the session, which ends the reader's processing.
    /// </summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!_stopped)
            {
                _stopped = true;
                Prepare(_properties);
                ControlTraceW(_handle, null, _properties, ControlStop);
            }
        }
    }

    /// <summary>
    /// Деструктор
    /// </summary>
    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            if (_properties != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_properties);
                _properties = IntPtr.Zero;
            }
        }
    }

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

    private static uint EnableAll(ulong handle, Guid provider) =>
        EnableTraceEx2(handle, ref provider, EnableProvider, LevelInformational, ulong.MaxValue, 0, 0, IntPtr.Zero);

    // Enables the provider with an event id filter of the one event: the filter and its descriptor in one block.
    private static uint EnableOne(ulong handle, Guid provider, ushort eventId)
    {
        var block = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteByte(block, 0, 1);
            Marshal.WriteByte(block, 1, 0);
            Marshal.WriteInt16(block, 2, 1);
            Marshal.WriteInt16(block, 4, (short)eventId);
            Marshal.WriteInt16(block, 6, 0);
            Marshal.WriteInt64(block, 8, 0);
            var descriptor = block + 16;
            Marshal.WriteInt64(descriptor, 0, block.ToInt64());
            Marshal.WriteInt32(descriptor, 8, EventFilterSize);
            Marshal.WriteInt32(descriptor, 12, unchecked((int)FilterTypeEventId));
            var parameters = new EnableParameters
            {
                Version = ParametersVersion,
                FilterDescriptors = descriptor,
                FilterDescriptorCount = 1,
            };
            return EnableTraceEx2(handle, ref provider, EnableProvider, LevelInformational, ulong.MaxValue, 0, 0, ref parameters);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private static IntPtr Properties()
    {
        var properties = Marshal.AllocHGlobal(PropertiesSize);
        Prepare(properties);
        return properties;
    }

    // EVENT_TRACE_PROPERTIES of the session: real time, one pool of small buffers, the timer at its floor.
    private static void Prepare(IntPtr properties)
    {
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
    }

    // ENABLE_TRACE_PARAMETERS.
    [StructLayout(LayoutKind.Sequential)]
    private struct EnableParameters
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public Guid SourceId;
        public IntPtr FilterDescriptors;
        public uint FilterDescriptorCount;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTraceW(out ulong handle, string name, IntPtr properties);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [LibraryImport("advapi32.dll", EntryPoint = "EnableTraceEx2")]
    private static partial uint EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong matchAny, ulong matchAll, uint timeout, ref EnableParameters parameters);
}
