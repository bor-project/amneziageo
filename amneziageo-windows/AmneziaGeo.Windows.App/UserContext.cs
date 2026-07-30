using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        return FromImpersonation(pipe) ?? FromClientProcess(pipe);
    }

    private static (string Sid, string Root)? FromImpersonation(NamedPipeServerStream pipe)
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

    // Impersonation only works when the client opened the pipe at impersonation level; the client's process token
    // needs no such cooperation, so an older or third-party client still resolves to its own library.
    private static (string Sid, string Root)? FromClientProcess(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid == 0)
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                using var identity = new WindowsIdentity(token);
                var sid = identity.User?.Value;
                var root = RootForToken(token);
                return sid is not null && root is not null ? (sid, root) : null;
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
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

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int TokenQuery = 0x0008;

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserProfileDirectory(IntPtr token, StringBuilder? path, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
