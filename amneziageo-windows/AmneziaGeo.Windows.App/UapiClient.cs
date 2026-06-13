using System.IO.Pipes;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Talks to the AmneziaWG device over its UAPI named pipe.
/// </summary>
internal static class UapiClient
{
    /// <summary>
    /// Adds an allowed IP to the peer identified by its base64 public key.
    /// </summary>
    public static bool AddAllowedIp(string tunnelName, string peerPublicKeyBase64, string cidr)
    {
        var peerHex = Convert.ToHexStringLower(Convert.FromBase64String(peerPublicKeyBase64));
        var request = $"set=1\npublic_key={peerHex}\nallowed_ip={cidr}\n\n";
        return Exchange(tunnelName, request).Contains("errno=0", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the raw device state from a get request.
    /// </summary>
    public static string Get(string tunnelName)
    {
        return Exchange(tunnelName, "get=1\n\n");
    }

    private static string Exchange(string tunnelName, string request)
    {
        var pipeName = $@"ProtectedPrefix\Administrators\AmneziaWG\{tunnelName}";
        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
        {
            client.Connect(5000);
            var payload = Encoding.UTF8.GetBytes(request);
            client.Write(payload, 0, payload.Length);
            client.Flush();

            var response = new StringBuilder();
            using (var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    response.AppendLine(line);
                    if (line.StartsWith("errno=", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }

            return response.ToString();
        }
    }
}
