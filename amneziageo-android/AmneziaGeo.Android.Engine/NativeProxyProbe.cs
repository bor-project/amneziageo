using System.Runtime.InteropServices;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Checks native QUIC authorization.
/// </summary>
public static partial class NativeProxyProbe
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Func<int, bool>? _protect;

    public static async Task<TransportProxyProbeResult> CheckAsync(string profile, Func<int, bool> protect, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _protect = protect;
            var result = await Task.Run(() => Probe(profile), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result == 1 ? new TransportProxyProbeResult(TransportProxyAvailability.Available, ProtocolVersion: 1)
                : new TransportProxyProbeResult(TransportProxyAvailability.Unavailable, result switch
                {
                    -2 => "proxy-auth", -3 => "proxy-tls", -4 => "bad-proxy", _ => "proxy-no-answer",
                });
        }
        finally
        {
            _protect = null;
            Gate.Release();
        }
    }

    private static unsafe int Probe(string profile) => ProbeNative(profile, &ProtectNative);

    [UnmanagedCallersOnly]
    private static int ProtectNative(int fd)
    {
        try
        {
            return _protect?.Invoke(fd) == true ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    [LibraryImport("amneziawg-go", EntryPoint = "agXHttpProbe", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int ProbeNative(string profile, delegate* unmanaged<int, int> protect);
}
