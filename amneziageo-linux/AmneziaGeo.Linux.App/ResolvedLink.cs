using System.Globalization;
using System.Net;
using Tmds.DBus.Protocol;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Steers systemd-resolved over the system bus: the resolver of the tunnel link and the reload of the service.
/// </summary>
internal static class ResolvedLink
{
    private const string ResolveService = "org.freedesktop.resolve1";
    private const string ResolvePath = "/org/freedesktop/resolve1";
    private const string ResolveManager = "org.freedesktop.resolve1.Manager";
    private const string SystemdService = "org.freedesktop.systemd1";
    private const string SystemdPath = "/org/freedesktop/systemd1";
    private const string SystemdManager = "org.freedesktop.systemd1.Manager";
    private const string ResolvedUnit = "systemd-resolved.service";
    private const int AddressFamilyInet = 2;

    // Writes the arguments of a method call.
    private delegate void Arguments(ref MessageWriter writer);

    /// <summary>
    /// Makes the server on the link the only resolver systemd-resolved asks; returns whether it stands.
    /// </summary>
    public static async Task<bool> PointAsync(string iface, IPAddress server, AgentLog log)
    {
        try
        {
            if (Index(iface) is not { } index)
            {
                log.Warn("dns", $"systemd-resolved still asks the network's resolvers beside {server}: {iface} has no index");
                return false;
            }

            using var connection = await ConnectAsync().ConfigureAwait(false);
            await connection.CallMethodAsync(Resolve(connection, "SetLinkDNS", "ia(iay)", (ref MessageWriter writer) =>
            {
                writer.WriteInt32(index);
                var servers = writer.WriteArrayStart(DBusType.Struct);
                writer.WriteStructureStart();
                writer.WriteInt32(AddressFamilyInet);
                writer.WriteArray(server.GetAddressBytes());
                writer.WriteArrayEnd(servers);
            })).ConfigureAwait(false);
            await connection.CallMethodAsync(Resolve(connection, "SetLinkDomains", "ia(sb)", (ref MessageWriter writer) =>
            {
                writer.WriteInt32(index);
                var domains = writer.WriteArrayStart(DBusType.Struct);
                writer.WriteStructureStart();
                writer.WriteString(".");
                writer.WriteBool(true);
                writer.WriteArrayEnd(domains);
            })).ConfigureAwait(false);
            await connection.CallMethodAsync(Resolve(connection, "SetLinkDefaultRoute", "ib", (ref MessageWriter writer) =>
            {
                writer.WriteInt32(index);
                writer.WriteBool(true);
            })).ConfigureAwait(false);
            log.Info("dns", $"systemd-resolved asks {server} on {iface} alone");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn("dns", $"systemd-resolved still asks the network's resolvers beside {server}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Drops the resolver settings of the link; a link that is gone is left alone.
    /// </summary>
    public static async Task RevertAsync(string iface, AgentLog log)
    {
        try
        {
            if (Index(iface) is not { } index)
            {
                return;
            }

            using var connection = await ConnectAsync().ConfigureAwait(false);
            await connection.CallMethodAsync(Resolve(connection, "RevertLink", "i", (ref MessageWriter writer) =>
            {
                writer.WriteInt32(index);
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Debug("dns", $"the resolver settings of {iface} stay in systemd-resolved: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes systemd-resolved read its configuration and the resolver file anew.
    /// </summary>
    public static async Task ReloadAsync(AgentLog log)
    {
        try
        {
            using var connection = await ConnectAsync().ConfigureAwait(false);
            await connection.CallMethodAsync(Call(connection, SystemdService, SystemdPath, SystemdManager, "ReloadOrTryRestartUnit", "ss", (ref MessageWriter writer) =>
            {
                writer.WriteString(ResolvedUnit);
                writer.WriteString("replace");
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Debug("dns", $"systemd-resolved was not reloaded: {ex.Message}");
        }
    }

    // Opens a connection of its own to the system bus.
    private static async Task<Connection> ConnectAsync()
    {
        var connection = new Connection(Address.System!);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // Builds a call to the resolve1 manager.
    private static MessageBuffer Resolve(Connection connection, string member, string signature, Arguments arguments) =>
        Call(connection, ResolveService, ResolvePath, ResolveManager, member, signature, arguments);

    // Builds a method call.
    private static MessageBuffer Call(Connection connection, string service, string path, string @interface, string member, string signature, Arguments arguments)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(service, path, @interface, member, signature, MessageFlags.None);
            arguments(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    // The kernel index of the interface, or null when it is gone.
    private static int? Index(string iface)
    {
        var path = $"/sys/class/net/{iface}/ifindex";
        if (!File.Exists(path) || !int.TryParse(File.ReadAllText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        return index;
    }
}
