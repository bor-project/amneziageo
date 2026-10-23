using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The VPNs of other programs on this machine: the adapters they stand on and the servers the connections of Windows
/// dial.
/// </summary>
internal static class OtherVpns
{
    /// <summary>
    /// A link of Windows: SSTP, L2TP, PPTP or IKEv2.
    /// </summary>
    public const string Ppp = "PPP";

    // What the description of an adapter of a known VPN client carries.
    private static readonly string[] Vendors = ["Cisco AnyConnect", "Cisco Secure Client", "Fortinet", "FortiClient", "TAP-Windows", "OpenVPN", "PANGP"];

    // The phonebooks of the connections, under the profile of a user and under the shared data.
    private const string PhonebookFolder = @"Microsoft\Network\Connections\Pbk";

    /// <summary>
    /// An adapter another VPN stands on, with its IPv4 address when it has one.
    /// </summary>
    public sealed record Adapter(uint Index, string Name, string Kind, IPAddress? Address);

    /// <summary>
    /// What a VPN adapter of another program is: <see cref="Ppp"/> for a link of Windows, the client for a known one,
    /// null for an adapter of ours or of no VPN.
    /// </summary>
    internal static string? KindOf(NetworkInterfaceType type, string name, string description)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel || RouteManager.IsTunnelAdapter(name, description))
        {
            return null;
        }

        if (type == NetworkInterfaceType.Ppp)
        {
            return Ppp;
        }

        foreach (var vendor in Vendors)
        {
            if (description.Contains(vendor, StringComparison.OrdinalIgnoreCase))
            {
                return vendor;
            }
        }

        return null;
    }

    /// <summary>
    /// The adapters other VPNs stand on now.
    /// </summary>
    public static IReadOnlyList<Adapter> Adapters()
    {
        var found = new List<Adapter>();
        foreach (var ni in NetworkAdapters.All())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || KindOf(ni.NetworkInterfaceType, ni.Name, ni.Description) is not { } kind)
            {
                continue;
            }

            try
            {
                var properties = ni.GetIPProperties();
                var address = properties.UnicastAddresses
                    .Select(unicast => unicast.Address)
                    .FirstOrDefault(one => one.AddressFamily == AddressFamily.InterNetwork);
                found.Add(new Adapter((uint)properties.GetIPv4Properties().Index, ni.Name, kind, address));
            }
            catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
            {
            }
        }

        return found;
    }

    /// <summary>
    /// The servers the VPN connections of Windows dial, from the phonebook of every user and the shared one.
    /// </summary>
    public static IReadOnlyList<string> Servers()
    {
        var servers = new List<string>();
        foreach (var path in Phonebooks())
        {
            try
            {
                foreach (var server in ParsePhonebook(File.ReadAllText(path)))
                {
                    if (!servers.Contains(server, StringComparer.OrdinalIgnoreCase))
                    {
                        servers.Add(server);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return servers;
    }

    /// <summary>
    /// The servers the VPN entries of a phonebook dial.
    /// </summary>
    internal static IReadOnlyList<string> ParsePhonebook(string text)
    {
        var servers = new List<string>();
        var vpn = false;
        var server = default(string);
        void Close()
        {
            if (vpn && server is { Length: > 0 } && !servers.Contains(server, StringComparer.OrdinalIgnoreCase))
            {
                servers.Add(server);
            }

            vpn = false;
            server = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Close();
                continue;
            }

            var split = line.IndexOf('=');
            if (split < 0)
            {
                continue;
            }

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            if (key.Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                vpn = value == "2";
            }
            else if (key.Equals("PhoneNumber", StringComparison.OrdinalIgnoreCase) && server is null)
            {
                server = value;
            }
        }

        Close();
        return servers;
    }

    /// <summary>
    /// The IPv4 addresses the servers answer to; a name that does not resolve is left out.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(IEnumerable<string> servers, CancellationToken ct)
    {
        var addresses = new List<IPAddress>();
        void Take(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork && !addresses.Contains(address))
            {
                addresses.Add(address);
            }
        }

        foreach (var server in servers)
        {
            if (IPAddress.TryParse(server, out var literal))
            {
                Take(literal);
                continue;
            }

            try
            {
                foreach (var address in await Dns.GetHostAddressesAsync(server, AddressFamily.InterNetwork, ct).ConfigureAwait(false))
                {
                    Take(address);
                }
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
            }
        }

        return addresses;
    }

    // The phonebook files: the shared folder and the one of every profile, each with its hidden part.
    private static IReadOnlyList<string> Phonebooks()
    {
        var folders = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), PhonebookFolder),
        };
        try
        {
            var users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
            foreach (var profile in Directory.EnumerateDirectories(users))
            {
                folders.Add(Path.Combine(profile, "AppData", "Roaming", PhonebookFolder));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var files = new List<string>();
        foreach (var folder in folders)
        {
            foreach (var part in new[] { folder, Path.Combine(folder, "_hiddenPbk") })
            {
                try
                {
                    if (Directory.Exists(part))
                    {
                        files.AddRange(Directory.EnumerateFiles(part, "*.pbk"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return files;
    }
}
