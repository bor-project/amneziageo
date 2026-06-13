using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.Engine;

/// <summary>
/// Managed wrapper over the AmneziaWG tunnel.dll native library.
/// </summary>
public static partial class WireGuardEngine
{
    /// <summary>
    /// Generates a Curve25519 key pair as base64 strings.
    /// </summary>
    public static (string PublicKey, string PrivateKey) GenerateKeypair()
    {
        var publicKey = new byte[32];
        var privateKey = new byte[32];
        GenerateKeypairNative(publicKey, privateKey);
        return (Convert.ToBase64String(publicKey), Convert.ToBase64String(privateKey));
    }

    /// <summary>
    /// Runs the tunnel as a Windows service until the SCM stops it.
    /// </summary>
    public static bool RunTunnelService(string config, string name)
    {
        return TunnelServiceNative(config, name);
    }

    [LibraryImport("tunnel.dll", EntryPoint = "WireGuardTunnelService", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool TunnelServiceNative(string config, string name);

    [LibraryImport("tunnel.dll", EntryPoint = "WireGuardGenerateKeypair")]
    private static partial void GenerateKeypairNative(Span<byte> publicKey, Span<byte> privateKey);
}
