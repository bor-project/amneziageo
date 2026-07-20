using System.Runtime.InteropServices;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Managed wrapper over the amneziawg-go c-shared native library.
/// </summary>
internal static partial class AwgEngine
{
    private const string Lib = "amneziawg-go";

    /// <summary>
    /// Starts the tunnel on an established tun fd; returns an engine handle.
    /// </summary>
    public static int TurnOn(string settings, int tunFd)
    {
        return TurnOnNative(settings, tunFd);
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

    [LibraryImport(Lib, EntryPoint = "wgTurnOn", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int TurnOnNative(string settings, int tunFd);

    [LibraryImport(Lib, EntryPoint = "wgTurnOff")]
    private static partial void TurnOffNative(int handle);

    [LibraryImport(Lib, EntryPoint = "wgGetConfig")]
    private static partial IntPtr GetConfigNative(int handle);

    [LibraryImport(Lib, EntryPoint = "wgGetSocketV4")]
    private static partial int GetSocketV4Native(int handle);
}