using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

internal interface ILocalDohResources
{
    string? Template(IPAddress address);
    string? Binding(IPAddress address);
    void RemoveTemplate(IPAddress address);
    void RemoveBinding(IPAddress address);
}

internal sealed class LocalDohOwnership : IDisposable
{
    private readonly string _path;
    private readonly Owner _owner;
    private readonly ILocalDohResources _resources;
    private readonly ILogger _logger;
    private FileStream? _turn;

    /// <summary>
    /// ctor
    /// </summary>
    private LocalDohOwnership(string path, Owner owner, FileStream turn, ILocalDohResources resources, ILogger logger)
    {
        _path = path;
        _owner = owner;
        _turn = turn;
        _resources = resources;
        _logger = logger;
    }

    public string AppId => _owner.AppId;

    public static LocalDohOwnership? Take(string root, string tunnel, IPAddress address, ILocalDohResources resources, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnel);
        if (!IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("The local resolver requires a loopback address.", nameof(address));
        }

        Directory.CreateDirectory(root);
        var stem = Path.Combine(root, Convert.ToHexString(address.GetAddressBytes()));
        var turn = TryLock(stem + ".lock");
        if (turn is null)
        {
            return null;
        }

        var path = stem + ".json";
        try
        {
            if (File.Exists(path))
            {
                var previous = Read(path, logger);
                if (previous is null || !Clear(path, previous, resources, logger))
                {
                    turn.Dispose();
                    return null;
                }
            }

            if (resources.Template(address) is not null || resources.Binding(address) is not null)
            {
                turn.Dispose();
                return null;
            }

            var appId = Guid.NewGuid().ToString("B");
            var owner = new Owner(tunnel, address.ToString(), appId, TemplateFor(address, appId));
            var temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(owner));
                File.Move(temporary, path, true);
            }
            finally
            {
                File.Delete(temporary);
            }

            return new LocalDohOwnership(path, owner, turn, resources, logger);
        }
        catch
        {
            turn.Dispose();
            throw;
        }
    }

    public static void Reconcile(string root, ILocalDohResources resources, ILogger logger,
        string? tunnel = null, IEnumerable<string>? keep = null, Func<bool>? abortIf = null)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var retained = new HashSet<string>(keep ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            if (abortIf?.Invoke() == true)
            {
                return;
            }

            using var turn = TryLock(Path.ChangeExtension(path, ".lock"));
            if (turn is null)
            {
                continue;
            }

            var owner = Read(path, logger);
            if (owner is null || retained.Contains(owner.Tunnel)
                || tunnel is not null && !string.Equals(tunnel, owner.Tunnel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Clear(path, owner, resources, logger, abortIf);
        }
    }

    public void Dispose()
    {
        var turn = Interlocked.Exchange(ref _turn, null);
        if (turn is null)
        {
            return;
        }

        try
        {
            var current = Read(_path, _logger);
            if (current == _owner)
            {
                Clear(_path, _owner, _resources, _logger);
            }
        }
        finally
        {
            turn.Dispose();
        }
    }

    internal static string TemplateFor(IPAddress address, string appId)
    {
        return new UriBuilder(Uri.UriSchemeHttps, address.ToString())
        {
            Path = "dns-query/" + Guid.Parse(appId).ToString("N"),
        }.Uri.AbsoluteUri;
    }

    private static bool Clear(string path, Owner owner, ILocalDohResources resources, ILogger logger, Func<bool>? abortIf = null)
    {
        try
        {
            var address = IPAddress.Parse(owner.Address);
            if (abortIf?.Invoke() == true)
            {
                return false;
            }

            if (string.Equals(resources.Template(address), owner.Template, StringComparison.OrdinalIgnoreCase))
            {
                if (abortIf?.Invoke() == true)
                {
                    return false;
                }

                resources.RemoveTemplate(address);
                if (string.Equals(resources.Template(address), owner.Template, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (abortIf?.Invoke() == true)
            {
                return false;
            }

            if (string.Equals(resources.Binding(address), owner.AppId, StringComparison.OrdinalIgnoreCase))
            {
                if (abortIf?.Invoke() == true)
                {
                    return false;
                }

                resources.RemoveBinding(address);
                if (string.Equals(resources.Binding(address), owner.AppId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (abortIf?.Invoke() == true)
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "local HTTPS resolver state for {Tunnel} could not be restored", owner.Tunnel);
            return false;
        }
    }

    private static Owner? Read(string path, ILogger logger)
    {
        try
        {
            var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(path));
            return owner is not null && !string.IsNullOrWhiteSpace(owner.Tunnel)
                && IPAddress.TryParse(owner.Address, out var address) && IPAddress.IsLoopback(address)
                && Guid.TryParse(owner.AppId, out _)
                && string.Equals(owner.Template, TemplateFor(address, owner.AppId), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileNameWithoutExtension(path), Convert.ToHexString(address.GetAddressBytes()), StringComparison.OrdinalIgnoreCase)
                ? owner : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "local HTTPS resolver ownership could not be read");
            return null;
        }
    }

    private static FileStream? TryLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Owner(string Tunnel, string Address, string AppId, string Template);
}
