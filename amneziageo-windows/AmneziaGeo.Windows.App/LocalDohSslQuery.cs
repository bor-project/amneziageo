using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Windows.App;

internal static partial class LocalDohSslQuery
{
    private const uint InitializeConfig = 2;
    private const int SslCertificate = 1;
    private const uint InsufficientBuffer = 122;

    internal delegate uint Query(ref SslQuery input, nint output, uint length, out uint required);

    public static string? Read(IPAddress address)
    {
        Check(HttpInitialize(new HttpVersion { Major = 1 }, InitializeConfig, 0));
        try
        {
            return Read(address, QueryNative);
        }
        finally
        {
            Check(HttpTerminate(InitializeConfig, 0));
        }
    }

    internal static string? Read(IPAddress address, Query query)
    {
        var socket = new IPEndPoint(address, 443).Serialize();
        var bytes = new byte[socket.Size];
        for (var at = 0; at < bytes.Length; at++)
        {
            bytes[at] = socket[at];
        }

        var key = Marshal.AllocHGlobal(bytes.Length);
        var output = nint.Zero;
        try
        {
            Marshal.Copy(bytes, 0, key, bytes.Length);
            var input = new SslQuery { Address = key };
            var size = 0u;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var code = query(ref input, output, size, out var required);
                if (code is 2 or 259)
                {
                    return null;
                }

                if (code is InsufficientBuffer or 234)
                {
                    if (required < Marshal.SizeOf<SslSet>() || required > 1024 * 1024 || required <= size)
                    {
                        throw new InvalidDataException("The HTTP SSL configuration query returned an invalid buffer size.");
                    }

                    Marshal.FreeHGlobal(output);
                    output = 0;
                    output = Marshal.AllocHGlobal(checked((int)required));
                    size = required;
                    continue;
                }

                Check(code);
                if (output == 0 || size < Marshal.SizeOf<SslSet>() || required > size)
                {
                    throw new InvalidDataException("The HTTP SSL configuration query returned an incomplete record.");
                }

                return Marshal.PtrToStructure<SslSet>(output).Parameters.AppId.ToString("B");
            }

            throw new IOException("The HTTP SSL configuration changed during its query.");
        }
        finally
        {
            Marshal.FreeHGlobal(output);
            Marshal.FreeHGlobal(key);
        }
    }

    private static uint QueryNative(ref SslQuery input, nint output, uint length, out uint required) =>
        HttpQueryServiceConfiguration(0, SslCertificate, ref input, (uint)Marshal.SizeOf<SslQuery>(), output, length, out required, 0);

    private static void Check(uint code)
    {
        if (code != 0)
        {
            throw new Win32Exception(unchecked((int)code));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HttpVersion
    {
        public ushort Major;
        public ushort Minor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SslQuery
    {
        public int QueryType;
        public nint Address;
        public uint Token;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SslSet
    {
        public nint Address;
        public SslParameters Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SslParameters
    {
        public uint HashLength;
        public nint Hash;
        public Guid AppId;
        public nint CertificateStore;
        public uint CheckMode;
        public uint Freshness;
        public uint RetrievalTimeout;
        public nint CtlIdentifier;
        public nint CtlStore;
        public uint Flags;
    }

    [LibraryImport("httpapi.dll")]
    private static partial uint HttpInitialize(HttpVersion version, uint flags, nint reserved);

    [LibraryImport("httpapi.dll")]
    private static partial uint HttpTerminate(uint flags, nint reserved);

    [LibraryImport("httpapi.dll")]
    private static partial uint HttpQueryServiceConfiguration(nint service, int config, ref SslQuery input,
        uint inputLength, nint output, uint outputLength, out uint required, nint overlapped);
}
