using System.Runtime.InteropServices;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Managed wrapper over the amneziawg-go c-shared native library.
/// </summary>
internal static partial class AwgEngine
{
    private const string Lib = "amneziawg-go";

    // Кто отпускает сокет мимо туннеля.
    private static Func<int, bool>? _protect;

    /// <summary>
    /// Движок молчит.
    /// </summary>
    public const int LogSilent = 0;

    /// <summary>
    /// Движок пишет только ошибки.
    /// </summary>
    public const int LogError = 1;

    /// <summary>
    /// Движок пишет каждое своё решение.
    /// </summary>
    public const int LogVerbose = 2;

    /// <summary>
    /// Starts the tunnel on an established tun fd; returns an engine handle.
    /// </summary>
    public static int TurnOn(string settings, int tunFd, int logLevel)
    {
        return TurnOnNative(settings, tunFd, logLevel);
    }

    /// <summary>
    /// Stops the tunnel by handle.
    /// </summary>
    public static void TurnOff(int handle)
    {
        TurnOffNative(handle);
    }

    /// <summary>
    /// Reads the running configuration over UAPI.
    /// </summary>
    public static string? GetConfig(int handle)
    {
        var ptr = GetConfigNative(handle);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    /// <summary>
    /// Returns the IPv4 handshake socket to protect from the tunnel.
    /// </summary>
    public static int GetSocketV4(int handle)
    {
        return GetSocketV4Native(handle);
    }

    /// <summary>
    /// Replaces the running configuration over UAPI.
    /// </summary>
    public static bool SetConfig(int handle, string settings)
    {
        return SetConfigNative(handle, settings) == 0;
    }

    /// <summary>
    /// Hands the shim the ranges it decides on its own, one "cidr=role" a line.
    /// </summary>
    public static bool SetVerdicts(int handle, string spec)
    {
        return SetVerdictsNative(handle, spec) == 0;
    }

    /// <summary>
    /// Tells the shim a tun of its own is about to be replaced, so a read failing meanwhile waits for the new one.
    /// </summary>
    public static bool PrepareSwap(int handle, bool pending)
    {
        return PrepareSwapNative(handle, pending ? 1 : 0) == 0;
    }

    /// <summary>
    /// Puts a freshly established tun under the running engine and closes the previous one.
    /// </summary>
    public static bool SwapTun(int handle, int tunFd)
    {
        return SwapTunNative(handle, tunFd) == 0;
    }

    /// <summary>
    /// Sets how long the shim keeps an address it has seen no traffic for.
    /// </summary>
    public static bool SetVerdictTtl(int handle, int seconds)
    {
        return SetVerdictTtlNative(handle, seconds) == 0;
    }

    /// <summary>
    /// Reads the addresses the session has touched, one "address role age" a line.
    /// </summary>
    public static string? LiveAddresses(int handle)
    {
        var ptr = LiveAddressesNative(handle);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    /// <summary>
    /// Reads what the verdict layer and its forwarder have counted.
    /// </summary>
    public static string? Stats(int handle)
    {
        var ptr = StatsNative(handle);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    /// <summary>
    /// Turns on the shim's own stack, which carries the streams a direct verdict names past the tunnel.
    /// </summary>
    public static bool SetTcpDirect(int handle, bool enabled)
    {
        return SetTcpDirectNative(handle, enabled ? 1 : 0) == 0;
    }

    /// <summary>
    /// Gives the shim the call that excuses a socket from the tunnel.
    /// </summary>
    public static unsafe bool SetProtector(int handle, Func<int, bool> protect)
    {
        _protect = protect;
        return SetProtectorNative(handle, &ProtectNative) == 0;
    }

    [UnmanagedCallersOnly]
    private static int ProtectNative(int fd)
    {
        var protect = _protect;
        if (protect is null)
        {
            return 0;
        }

        try
        {
            return protect(fd) ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    [LibraryImport(Lib, EntryPoint = "wgTurnOn", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int TurnOnNative(string settings, int tunFd, int logLevel);

    [LibraryImport(Lib, EntryPoint = "wgTurnOff")]
    private static partial void TurnOffNative(int handle);

    [LibraryImport(Lib, EntryPoint = "wgGetConfig")]
    private static partial IntPtr GetConfigNative(int handle);

    [LibraryImport(Lib, EntryPoint = "wgGetSocketV4")]
    private static partial int GetSocketV4Native(int handle);

    [LibraryImport(Lib, EntryPoint = "wgSetConfig", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetConfigNative(int handle, string settings);

    [LibraryImport(Lib, EntryPoint = "wgSetVerdicts", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetVerdictsNative(int handle, string spec);

    [LibraryImport(Lib, EntryPoint = "wgSetTcpDirect")]
    private static partial int SetTcpDirectNative(int handle, int enable);

    [LibraryImport(Lib, EntryPoint = "wgPrepareSwap")]
    private static partial int PrepareSwapNative(int handle, int pending);

    [LibraryImport(Lib, EntryPoint = "wgSwapTun")]
    private static partial int SwapTunNative(int handle, int tunFd);

    [LibraryImport(Lib, EntryPoint = "wgSetVerdictTtl")]
    private static partial int SetVerdictTtlNative(int handle, int seconds);

    [LibraryImport(Lib, EntryPoint = "wgTunnelStats")]
    private static partial IntPtr StatsNative(int handle);

    [LibraryImport(Lib, EntryPoint = "wgLiveAddresses")]
    private static partial IntPtr LiveAddressesNative(int handle);

    [LibraryImport(Lib, EntryPoint = "wgSetProtector")]
    private static unsafe partial int SetProtectorNative(int handle, delegate* unmanaged<int, int> protect);
}