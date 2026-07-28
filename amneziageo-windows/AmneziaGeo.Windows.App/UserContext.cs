using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Resolves the OS identity and data root of a connecting pipe client.
/// </summary>
internal static class UserContext
{
    /// <summary>
    /// Returns the connecting client's SID and per-user data root, or null when it cannot be resolved.
    /// </summary>
    public static (string Sid, string Root)? ResolveClient(NamedPipeServerStream pipe)
    {
        string? sid = null;
        string? root = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value;
                root = RootForToken(identity.Token);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }

        return sid is not null && root is not null ? (sid, root) : null;
    }

    private static string? RootForToken(IntPtr token)
    {
        var size = 0u;
        _ = GetUserProfileDirectory(token, null, ref size);
        if (size == 0)
        {
            return null;
        }

        var buffer = new StringBuilder((int)size);
        if (!GetUserProfileDirectory(token, buffer, ref size))
        {
            return null;
        }

        return AppDataRoot.UserBase(Path.Combine(buffer.ToString(), "AppData", "Local"));
    }

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserProfileDirectory(IntPtr token, StringBuilder? path, ref uint size);
}
