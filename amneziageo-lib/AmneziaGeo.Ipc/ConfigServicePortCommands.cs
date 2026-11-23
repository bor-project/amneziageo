using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Stores a configuration's service port.
/// </summary>
public static class ConfigServicePortCommands
{
    public static async Task<IpcAck> SetAsync(IStateStore store, IReadOnlyList<string> args, bool exists, CancellationToken ct = default)
    {
        if (args.Count != 2 || !exists)
        {
            return new IpcAck(false, "unknown-config");
        }

        var port = ConfigTransport.PortSent(args[1]);
        if (port < 0)
        {
            return new IpcAck(false, "invalid services port (1-65535)");
        }

        var previous = await store.GetConfigTransportAsync(args[0], ct).ConfigureAwait(false) ?? new ConfigTransport(args[0], false, 0);
        if (previous.ServicesPort != port)
        {
            await store.SetConfigTransportAsync(previous with { ServicesPort = port }, ct).ConfigureAwait(false);
        }

        return new IpcAck(true, string.Empty);
    }
}
