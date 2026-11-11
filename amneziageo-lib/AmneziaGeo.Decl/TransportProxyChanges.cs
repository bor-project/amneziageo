using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Decl;

/// <summary>
/// Compares the transport proxies selected by running configurations.
/// </summary>
public sealed class TransportProxyChanges
{
    private readonly IStateStore _store;
    private readonly IReadOnlyDictionary<string, string> _before;
    private readonly IReadOnlyDictionary<string, RunningTransportProxy>? _actual;

    /// <summary>
    /// ctor
    /// </summary>
    private TransportProxyChanges(IStateStore store, IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, RunningTransportProxy>? actual)
    {
        _store = store;
        _before = before;
        _actual = actual;
    }

    /// <summary>
    /// Captures the selected connection parameters.
    /// </summary>
    public static async Task<TransportProxyChanges> CaptureAsync(IStateStore store, IReadOnlyCollection<string> configs,
        CancellationToken ct = default, IReadOnlyDictionary<string, RunningTransportProxy>? actual = null)
    {
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var config in configs.Distinct(StringComparer.Ordinal))
        {
            before[config] = await StampAsync(store, config, ct, actual?.GetValueOrDefault(config)).ConfigureAwait(false);
        }

        return new TransportProxyChanges(store, before, actual);
    }

    /// <summary>
    /// Lists configurations whose selected transport changed.
    /// </summary>
    public async Task<IReadOnlyList<string>> ChangedAsync(CancellationToken ct = default)
    {
        var changed = new List<string>();
        foreach (var pair in _before)
        {
            if (pair.Value != await StampAsync(_store, pair.Key, ct, _actual?.GetValueOrDefault(pair.Key)).ConfigureAwait(false))
            {
                changed.Add(pair.Key);
            }
        }

        return changed;
    }

    /// <summary>
    /// Compares proxy connection parameters.
    /// </summary>
    public static bool SameDefinition(OfferedProxy first, OfferedProxy second) => Definition(first) == Definition(second);

    private static async Task<string> StampAsync(IStateStore store, string config, CancellationToken ct, RunningTransportProxy? actual)
    {
        var catalog = TransportProxyCatalog.For(store);
        var selection = default(TransportProxySelection);
        try
        {
            selection = actual is null ? await catalog.SelectionAsync(config, ct).ConfigureAwait(false)
                : new TransportProxySelection(config, actual.Reference);
            var resolved = actual is null ? await catalog.ResolveAsync(config, ct).ConfigureAwait(false)
                : await catalog.ResolveRunningAsync(config, actual, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                return string.Empty;
            }

            return Hash(new
            {
                selection.Reference,
                resolved.Record.Legacy,
                resolved.Record.LegacyOffered,
                Target = Host(resolved.TargetHost),
                resolved.TargetPort,
                Proxy = Definition(resolved.Record.Proxy),
            });
        }
        catch (TransportProxyFault ex)
        {
            return Hash(new { selection?.Reference, Fault = ex.Code });
        }
    }

    private static string Host(string host) => host.Trim('[', ']').ToUpperInvariant();

    private static OfferedProxy Definition(OfferedProxy proxy) => proxy with
    {
        Id = string.Empty,
        Name = string.Empty,
        Updated = DateTimeOffset.MinValue,
        Targets = null,
        Host = Host(proxy.Host),
        Tls = proxy.Tls with { ServerName = Host(proxy.Tls.ServerName), Pin = proxy.Tls.Pin.ToUpperInvariant() },
    };

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
