using System.Runtime.InteropServices;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Hosts XHTTP in the tunnel process.
/// </summary>
internal sealed partial class XHttpEngine : IDisposable
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> Pending = [];
    private static readonly HashSet<int> Active = [];
    private static Func<int, bool>? _protect;
    private int _handle;

    /// <summary>
    /// ctor
    /// </summary>
    private XHttpEngine(int handle, int localPort)
    {
        _handle = handle;
        LocalPort = localPort;
    }

    public int LocalPort { get; }

    public static unsafe XHttpEngine Start(string profile, int localPort, Func<int, bool> protect)
    {
        lock (Gate)
        {
            foreach (var previous in Pending.ToArray())
            {
                if (StopNative(previous, 1) == 0)
                {
                    Pending.Remove(previous);
                    Active.Remove(previous);
                }
            }

            if (Pending.Count > 0)
            {
                throw new TransportProxyFault("proxy-stopping");
            }

            if (Active.Count > 0 && (_protect is not { } current
                || !ReferenceEquals(current.Target, protect.Target) || current.Method != protect.Method))
            {
                throw new TransportProxyFault("proxy-protect-mismatch");
            }

            _protect = protect;
            var handle = StartNative(profile, &ProtectNative);
            if (handle <= 0)
            {
                throw new TransportProxyFault("proxy-start-failed");
            }

            Active.Add(handle);
            return new XHttpEngine(handle, localPort);
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle <= 0)
        {
            return;
        }

        if (StopNative(handle, 3000) == 0)
        {
            lock (Gate)
            {
                Active.Remove(handle);
            }
        }
        else
        {
            lock (Gate)
            {
                Pending.Add(handle);
            }

            _ = ReapAsync(handle);
        }
    }

    private static async Task ReapAsync(int handle)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (StopNative(handle, 100) == 0)
            {
                lock (Gate)
                {
                    Pending.Remove(handle);
                    Active.Remove(handle);
                }

                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    [UnmanagedCallersOnly]
    private static int ProtectNative(int fd)
    {
        try
        {
            return Volatile.Read(ref _protect)?.Invoke(fd) == true ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    [LibraryImport("amneziawg-go", EntryPoint = "agXHttpStart", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StartNative(string profile, delegate* unmanaged<int, int> protect);

    [LibraryImport("amneziawg-go", EntryPoint = "agXHttpStop")]
    private static partial int StopNative(int handle, int waitMs);
}
