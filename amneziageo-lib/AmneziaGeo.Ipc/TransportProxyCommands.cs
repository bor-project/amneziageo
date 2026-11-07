using System.Text.Json;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Handles common transport proxy commands.
/// </summary>
public static class TransportProxyCommands
{
    /// <summary>
    /// Executes a transport proxy command.
    /// </summary>
    public static async Task<IpcAck?> TryExecuteAsync(IStateStore store, IpcCommand command, CancellationToken ct = default,
        Func<CatalogTransportProxy, CancellationToken, Task<TransportProxyProbeResult>>? probe = null,
        IReadOnlyCollection<string>? running = null, Action<string>? changed = null,
        IReadOnlyDictionary<string, RunningTransportProxy>? actual = null)
    {
        var before = command.Op is IpcContract.OpSaveTransportProxy or IpcContract.OpRemoveTransportProxy or IpcContract.OpSelectTransportProxy
            && changed is not null
            ? await TransportProxyChanges.CaptureAsync(store, running ?? [], ct,
                command.Op == IpcContract.OpSelectTransportProxy ? null : actual).ConfigureAwait(false)
            : null;
        var ack = await ExecuteAsync(store, command, ct, probe).ConfigureAwait(false);
        if (ack is { Ok: true } && before is not null)
        {
            foreach (var config in await before.ChangedAsync(ct).ConfigureAwait(false))
            {
                changed!(config);
            }
        }

        return ack;
    }

    private static async Task<IpcAck?> ExecuteAsync(IStateStore store, IpcCommand command, CancellationToken ct,
        Func<CatalogTransportProxy, CancellationToken, Task<TransportProxyProbeResult>>? probe)
    {
        if (command.Op is not (IpcContract.OpListTransportProxies or IpcContract.OpGetTransportProxy
            or IpcContract.OpSaveTransportProxy or IpcContract.OpRemoveTransportProxy
            or IpcContract.OpSelectTransportProxy or IpcContract.OpProbeTransportProxy))
        {
            return null;
        }

        try
        {
            var catalog = TransportProxyCatalog.For(store);
            var args = command.Args;
            switch (command.Op)
            {
                case IpcContract.OpListTransportProxies when args.Count == 0:
                    return Reply(await catalog.ListAsync(ct).ConfigureAwait(false));

                case IpcContract.OpGetTransportProxy when args.Count == 1:
                    return await catalog.LocalAsync(Reference(args[0]), ct).ConfigureAwait(false) is { } local
                        ? Reply(local) : new IpcAck(false, "proxy-not-local");

                case IpcContract.OpSaveTransportProxy when args.Count == 2:
                    var draft = JsonSerializer.Deserialize<OfferedProxy>(args[1], IpcJson.Options)
                        ?? throw new TransportProxyFault("bad-proxy");
                    return Reply(await catalog.SaveLocalAsync(args[0].Length == 0 ? null : args[0], draft, ct).ConfigureAwait(false));

                case IpcContract.OpRemoveTransportProxy when args.Count == 1:
                    await catalog.RemoveLocalAsync(Reference(args[0]), ct).ConfigureAwait(false);
                    return new IpcAck(true, "");

                case IpcContract.OpSelectTransportProxy when args.Count == 2:
                    await catalog.SelectAsync(args[0], args[1] == "null" ? null : Reference(args[1]), ct).ConfigureAwait(false);
                    return new IpcAck(true, "");

                case IpcContract.OpProbeTransportProxy when args.Count == 1:
                    return Reply(await catalog.ProbeAsync(Reference(args[0]), probe ?? (async (record, token) =>
                        await TransportProxyProbes.CheckAsync(record, token, legacyConfig: record.Legacy
                            ? await store.GetConfigTextAsync(record.LegacyPublisher, token).ConfigureAwait(false) : null).ConfigureAwait(false)), ct).ConfigureAwait(false));

                default:
                    return new IpcAck(false, "bad-proxy-command");
            }
        }
        catch (TransportProxyFault ex)
        {
            return new IpcAck(false, ex.Code);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException)
        {
            return new IpcAck(false, "bad-proxy");
        }
    }

    private static ProxyReference Reference(string payload)
    {
        var reference = JsonSerializer.Deserialize<ProxyReference>(payload, IpcJson.Options);
        if (reference is null || !Guid.TryParse(reference.Source, out var source) || source == Guid.Empty
            || reference.Id is not { Length: > 0 and <= 64 }
            || !reference.Id.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_'))
        {
            throw new TransportProxyFault("bad-proxy-reference");
        }

        return reference with { Source = source.ToString("D") };
    }

    private static IpcAck Reply<T>(T value) => new(true, JsonSerializer.Serialize(value, IpcJson.Options));
}
