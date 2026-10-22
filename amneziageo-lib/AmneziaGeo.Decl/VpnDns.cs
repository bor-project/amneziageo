using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Decl;

/// <summary>
/// What the names that go through the VPN of a config are asked of.
/// </summary>
public enum VpnDnsMode
{
    /// <summary>
    /// What the server of the config picked, else the DNS line of the config.
    /// </summary>
    Default = 0,

    /// <summary>
    /// The name servers of the network of the device.
    /// </summary>
    Network = 1,

    /// <summary>
    /// The resolver of the server inside the tunnel.
    /// </summary>
    Server = 2,

    /// <summary>
    /// The name servers written with the choice.
    /// </summary>
    Own = 3,
}

/// <summary>
/// Where the names that go through the VPN of a config are asked.
/// </summary>
/// <param name="Network">Whether they are asked of the name servers of the network of the device.</param>
/// <param name="Servers">The name servers they are asked of otherwise, none where neither the choice nor the config names any.</param>
public sealed record VpnDnsPick(bool Network, IReadOnlyList<string> Servers);

/// <summary>
/// What the interface shows of the choice of a config.
/// </summary>
/// <param name="Shown">The servers the choice held settles on, empty where it settles on the network of the device.</param>
/// <param name="Network">Whether the choice held settles on the network of the device.</param>
/// <param name="Server">The resolver the server offers inside the tunnel, empty where it offers none.</param>
/// <param name="Default">The servers the default choice settles on, empty where it settles on the network of the device.</param>
/// <param name="DefaultNetwork">Whether the default choice settles on the network of the device.</param>
public sealed record VpnDnsShowing(string Shown, bool Network, string Server, string Default, bool DefaultNetwork);

/// <summary>
/// Picks the name servers of the names that go through the VPN, the one rule every platform takes.
/// </summary>
public static class VpnDns
{
    /// <summary>
    /// The name servers a choice of its own takes while it names none.
    /// </summary>
    public static readonly IReadOnlyList<string> Usual = ["1.1.1.1", "1.0.0.1"];

    /// <summary>
    /// The most name servers a choice of its own takes.
    /// </summary>
    public const int MaxServers = 8;

    private static readonly char[] Breaks = [',', ' ', ';', '\t', '\n', '\r'];

    private static readonly string[] Names = ["default", "network", "server", "own"];

    /// <summary>
    /// Returns where the names that go through the VPN are asked: the choice held for the config, then what the server
    /// offers, then the DNS line of the config.
    /// </summary>
    public static VpnDnsPick Pick(ConfigDns? held, OfferedDns? offer, IReadOnlyList<string> line)
    {
        ArgumentNullException.ThrowIfNull(line);

        switch (held?.VpnMode ?? VpnDnsMode.Default)
        {
            case VpnDnsMode.Network:
                return new VpnDnsPick(true, []);
            case VpnDnsMode.Own:
                var own = Servers(held!.VpnServers);
                return new VpnDnsPick(false, own.Count > 0 ? own : Usual);
            case VpnDnsMode.Server when offer is { } offered && HasV4(offered.Resolver):
                return new VpnDnsPick(false, offered.Resolver);
        }

        if (offer is { Mode: OfferedDns.NetworkMode })
        {
            return new VpnDnsPick(true, []);
        }

        return new VpnDnsPick(false, offer is { Servers.Count: > 0 } ? offer.Servers : line);
    }

    /// <summary>
    /// Returns the words a choice goes by in an answer: the choice and the servers of its own.
    /// </summary>
    public static string Describe(VpnDnsMode mode, string? servers) =>
        mode == VpnDnsMode.Own && servers is { Length: > 0 } ? $"{NameOf(mode)} {servers}" : NameOf(mode);

    /// <summary>
    /// Saves the name servers of a config, the parts given and the held ones otherwise, and drops the record once it
    /// holds nothing of its own.
    /// </summary>
    public static async Task SaveAsync(IStateStore store, string name, string? direct, VpnDnsMode? mode, string? servers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var held = await store.GetConfigDnsAsync(name, ct).ConfigureAwait(false);
        var next = new ConfigDns(
            name,
            direct?.Trim() ?? held?.Servers ?? string.Empty,
            mode ?? held?.VpnMode ?? VpnDnsMode.Default,
            servers?.Trim() ?? held?.VpnServers ?? string.Empty);
        if (next.Servers.Length == 0 && next.VpnMode == VpnDnsMode.Default && next.VpnServers.Length == 0)
        {
            await store.RemoveConfigDnsAsync(name, ct).ConfigureAwait(false);
        }
        else
        {
            await store.SetConfigDnsAsync(next, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns what the interface shows of the choice held and of the default one.
    /// </summary>
    public static VpnDnsShowing Showing(ConfigDns? held, OfferedDns? offer, IReadOnlyList<string> line)
    {
        var pick = Pick(held, offer, line);
        var usual = Pick(null, offer, line);

        var server = offer is { } offered && HasV4(offered.Resolver) ? string.Join(", ", offered.Resolver) : string.Empty;

        return new VpnDnsShowing(Text(pick), pick.Network, server, Text(usual), usual.Network);

        static string Text(VpnDnsPick one) => one.Network ? string.Empty : string.Join(", ", one.Servers.Count > 0 ? one.Servers : Usual);
    }

    /// <summary>
    /// Tells whether a list of name servers holds an IPv4 one, the kind a tunnel asks.
    /// </summary>
    public static bool HasV4(IReadOnlyList<string> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        return servers.Any(one => IPAddress.TryParse(one, out var address) && address.AddressFamily == AddressFamily.InterNetwork);
    }

    /// <summary>
    /// Tells whether a text names servers and none of them is IPv4.
    /// </summary>
    public static bool LacksV4(string? text) => Servers(text) is { Count: > 0 } listed && !HasV4(listed);

    /// <summary>
    /// Returns the addresses a text names, the ones that are not addresses left out.
    /// </summary>
    public static IReadOnlyList<string> Servers(string? text) =>
    [
        .. (text ?? string.Empty)
            .Split(Breaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxServers),
    ];

    /// <summary>
    /// Returns the word a choice goes by in a command.
    /// </summary>
    public static string NameOf(VpnDnsMode mode) => mode is >= VpnDnsMode.Default and <= VpnDnsMode.Own ? Names[(int)mode] : Names[0];

    /// <summary>
    /// Reads the word of a choice; null for a word that names none.
    /// </summary>
    public static VpnDnsMode? Read(string? word)
    {
        var index = Array.IndexOf(Names, (word ?? string.Empty).Trim().ToLowerInvariant());

        return index < 0 ? null : (VpnDnsMode)index;
    }

    /// <summary>
    /// Returns the words of a text that are not addresses; none when every word is one.
    /// </summary>
    public static IReadOnlyList<string> Odd(string? text) =>
    [
        .. (text ?? string.Empty)
            .Split(Breaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(one => !IsAddress(one)),
    ];

    // Whether a word is an address written in full.
    private static bool IsAddress(string word) =>
        IPAddress.TryParse(word, out var address)
        && (address.AddressFamily == AddressFamily.InterNetworkV6 || address.ToString() == word);
}
