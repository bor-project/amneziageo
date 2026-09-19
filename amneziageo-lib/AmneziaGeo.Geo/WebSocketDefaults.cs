using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// Takes the websocket front a server of ours offers as the websocket settings of its configuration.
/// </summary>
public static class WebSocketDefaults
{
    /// <summary>
    /// The port of a transport stored before any front was named.
    /// </summary>
    public const int DefaultPort = 443;

    /// <summary>
    /// The setting that keeps the marks of the fronts written into the configurations.
    /// </summary>
    public const string WrittenKey = "websocket.fronts";

    // How many marks of written fronts are kept.
    private const int Kept = 64;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Writes the offered front into the transport of the configuration; returns the transport written, or null when it stays.
    /// </summary>
    public static async Task<ConfigTransport?> FollowAsync(IStateStore store, ServerOffer offer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(offer);

        if (offer.Config.Length == 0 || WebSocketArgs.Of(offer) is not { } args)
        {
            return null;
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var text = await store.GetConfigTextAsync(offer.Config, ct).ConfigureAwait(false);
            if (text is not { Length: > 0 })
            {
                return null;
            }

            var stored = await store.GetConfigTransportAsync(offer.Config, ct).ConfigureAwait(false);
            var written = Marks(await store.GetSettingAsync(WrittenKey, ct).ConfigureAwait(false));
            var updated = Follow(offer.Config, stored, text, args, written);
            if (updated is not null)
            {
                await store.SetConfigTransportAsync(updated, ct).ConfigureAwait(false);
            }

            var front = Front(text, args);
            var named = (updated ?? stored)?.WebSocketHost ?? string.Empty;
            if (front.Length > 0 && string.Equals(named, front, StringComparison.Ordinal) && !written.Contains(Mark(front)))
            {
                written.Insert(0, Mark(front));
                await store.SetSettingAsync(WrittenKey, string.Join(' ', written.Take(Kept)), ct).ConfigureAwait(false);
            }

            return updated;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Returns the transport with the offered front, or null when the stored settings are the user's own, already name
    /// the front, or the front hands the tunnel to a port the configuration does not dial.
    /// </summary>
    public static ConfigTransport? Follow(
        string config, ConfigTransport? stored, string text, WebSocketArgs args, IReadOnlyCollection<string>? written = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var front = Front(text, args);
        if (front.Length == 0)
        {
            return null;
        }

        var endpointHost = Endpoint(WgConfigEditor.GetEndpoint(text ?? string.Empty) ?? string.Empty).Host;
        var host = args.Host.Length > 0 ? args.Host : endpointHost;
        var current = stored ?? new ConfigTransport(config, false, string.Empty, DefaultPort, Mtu: 0);
        if (!Follows(current, endpointHost, host, args.Port, written ?? []))
        {
            return null;
        }

        var updated = current with { WebSocketHost = front, WebSocketPort = args.Port };

        return updated == current ? null : updated;
    }

    /// <summary>
    /// Returns the address of the offered front for a configuration, empty when the front hands the tunnel to a port
    /// the configuration does not dial or names no host.
    /// </summary>
    public static string Front(string text, WebSocketArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var (endpointHost, endpointPort) = Endpoint(WgConfigEditor.GetEndpoint(text ?? string.Empty) ?? string.Empty);
        if (args.Target > 0 && args.Target != endpointPort)
        {
            return string.Empty;
        }

        var host = args.Host.Length > 0 ? args.Host : endpointHost;

        return host.Length == 0 ? string.Empty : Address(host, args.Port, args.Path);
    }

    /// <summary>
    /// Returns the mark a written front is remembered by.
    /// </summary>
    public static string Mark(string address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((address ?? string.Empty).Trim())), 0, 16);

    /// <summary>
    /// Returns the address a transport stores for a front.
    /// </summary>
    public static string Address(string host, int port, string path)
    {
        var shown = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

        return string.Create(CultureInfo.InvariantCulture, $"wss://{shown}:{port}/{path}");
    }

    // Tells whether the stored settings stand at their defaults, hold a front written before or name the front of the server.
    private static bool Follows(ConfigTransport transport, string endpointHost, string host, int port, IReadOnlyCollection<string> written)
    {
        if (transport.WebSocketHost.Trim().Length == 0 || written.Contains(Mark(transport.WebSocketHost), StringComparer.Ordinal))
        {
            return true;
        }

        var front = WsEndpoint.Parse(transport.WebSocketHost, transport.WebSocketPort, endpointHost);

        return front.Credentials.Length == 0
            && (Same(front.Host, host) || Same(front.Host, endpointHost))
            && (front.PathPrefix.Length == 0 || front.Port == port);
    }

    // Reads the marks of the fronts written, the latest first.
    private static List<string> Marks(string? value) =>
        [.. (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // Compares two hosts the way names compare, brackets aside.
    private static bool Same(string one, string two) =>
        string.Equals(one.Trim('[', ']'), two.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);

    // Splits the Endpoint of a text into its host and port.
    private static (string Host, int Port) Endpoint(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0)
        {
            return (endpoint.Trim(), 0);
        }

        var port = int.TryParse(endpoint[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        return (endpoint[..colon].Trim().Trim('[', ']'), port);
    }
}
