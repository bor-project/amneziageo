using System.ComponentModel;
using System.Globalization;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Keeps the named applications past a tunnel that carries everything. The kernel does the steering: the processes
/// live in a cgroup, a netfilter rule marks what its sockets send, and the mark selects the routes of the machine
/// narrower than a half of the address space and then a table whose default is the hop the machine reaches the
/// internet over. Own cgroup, netfilter table, routing table and rules; those of other software are never touched.
/// </summary>
internal sealed class AppBypass : IDisposable
{
    /// <summary>
    /// The routing table the mark selects.
    /// </summary>
    public const string Table = "51822";

    private const string Mark = SteeringRules.DirectMark;
    private const int OwnRulePriority = 5050;
    private const int HopRulePriority = 5051;
    private const string FilterRoot = "/proc/sys/net/ipv4/conf";
    private const string Fallback = "the applications the list keeps direct ride the tunnel with everything else";

    private readonly string _iface;
    private readonly string _gateway;
    private readonly string _device;
    private readonly AppGroup _group;
    private readonly AgentLog _log;
    private string? _strict;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    private AppBypass(string interfaceName, AppImages images, string gateway, string device, AgentLog log)
    {
        _iface = interfaceName;
        _gateway = gateway;
        _device = device;
        _group = new AppGroup(SteeringRules.DirectGroup, images, "leaves past the tunnel from now on", log);
        _log = log;
    }

    /// <summary>
    /// Raises the path past the tunnel for the applications the rules name; null when they name none, the kernel
    /// offers no unified cgroups, or the system refuses the mark or its routes.
    /// </summary>
    public static async Task<AppBypass?> TryStartAsync(string interfaceName, IReadOnlyList<string> apps, string gateway, string device, AgentLog log, CancellationToken ct)
    {
        var images = AppImages.Parse(apps);
        if (images.Empty)
        {
            return null;
        }

        if (!AppGroup.Offered)
        {
            log.Warn("apps", $"the kernel offers no unified cgroups here, so {Fallback}");
            return null;
        }

        var bypass = new AppBypass(interfaceName, images, gateway, device, log);
        if (!await bypass.RaiseAsync(ct).ConfigureAwait(false))
        {
            await bypass.StopAsync().ConfigureAwait(false);
            return null;
        }

        bypass._group.Watch();
        log.Info("apps", $"{images.Count} application(s) leave past {interfaceName} by cgroup, through {gateway} on {device}, and the rest of the machine rides the tunnel");
        return bypass;
    }

    /// <summary>
    /// Takes the path down and lets the processes back onto the routes of the machine.
    /// </summary>
    public async Task StopAsync()
    {
        await _group.StopAsync().ConfigureAwait(false);
        await ClearAsync(CancellationToken.None).ConfigureAwait(false);
        Tighten();
        _group.Remove();
    }

    /// <summary>
    /// Drops what a killed run left standing: the mark, the routing rules, the routing table and the cgroup.
    /// </summary>
    public static async Task RemoveAsync(CancellationToken ct)
    {
        await ClearAsync(ct).ConfigureAwait(false);
        var path = AppGroup.PathOf(SteeringRules.DirectGroup);
        AppGroup.Release(path);
        AppGroup.Remove(path);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _group.Dispose();
    }

    // Builds the cgroup, the mark and the routes the mark selects.
    private async Task<bool> RaiseAsync(CancellationToken ct)
    {
        if (!_group.Make())
        {
            return false;
        }

        await ClearAsync(ct).ConfigureAwait(false);
        var six = await SixAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> devices = six.Device is { } other && other != _device ? [_device, other] : [_device];
        if (!await MarkAsync(devices, six.Tunnelled && six.Device is null, ct).ConfigureAwait(false))
        {
            return false;
        }

        string[][] steps =
        [
            ["-4", "rule", "add", "fwmark", Mark, "lookup", SteeringRules.MainTable, "suppress_prefixlength", "1", "priority", Priority(OwnRulePriority)],
            ["-4", "rule", "add", "fwmark", Mark, "lookup", Table, "priority", Priority(HopRulePriority)],
            ["-4", "route", "replace", "default", "via", _gateway, "dev", _device, "table", Table],
        ];
        foreach (var step in steps)
        {
            var (exitCode, output) = await Shell.RunAsync("ip", ct, step).ConfigureAwait(false);
            if (exitCode != 0)
            {
                _log.Warn("apps", $"{Fallback}: ip {string.Join(' ', step)} failed: {output}");
                return false;
            }
        }

        Loosen();
        await LaySixAsync(six, ct).ConfigureAwait(false);
        return true;
    }

    // Marks what the sockets of the cgroup send and rewrites its source on the way out.
    private async Task<bool> MarkAsync(IReadOnlyList<string> devices, bool refuseSix, CancellationToken ct)
    {
        var path = Path.Combine(AgentPaths.Root, "direct-apps.nft");
        try
        {
            var text = SteeringRules.Passes(SteeringRules.DirectGroup, devices, refuseSix ? _iface : null);
            await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
            var (exitCode, output) = await Shell.RunAsync("nft", ct, "-f", path).ConfigureAwait(false);
            if (exitCode != 0)
            {
                _log.Warn("apps", $"{Fallback}: netfilter refused the mark: {output}");
            }

            return exitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Warn("apps", $"{Fallback}: the mark could not be written: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // What the sixth family of the applications needs: whether the tunnel carries it, and the hop of the machine for it.
    private async Task<(bool Tunnelled, string? Via, string? Device)> SixAsync(CancellationToken ct)
    {
        var carried = await Shell.RunAsync("ip", ct, "-6", "route", "show", "dev", _iface).ConfigureAwait(false);
        var tunnelled = carried.ExitCode == 0 && carried.Output.Split('\n').Any(line => line.StartsWith("::/1 ", StringComparison.Ordinal));
        if (!tunnelled)
        {
            return (false, null, null);
        }

        var lookup = await Shell.RunAsync("ip", ct, "-6", "route", "show", "default").ConfigureAwait(false);
        if (lookup.ExitCode != 0)
        {
            return (true, null, null);
        }

        foreach (var line in lookup.Output.Split('\n'))
        {
            if (Shell.Token(line, "dev") is { } device && device != _iface)
            {
                return (true, Shell.Token(line, "via"), device);
            }
        }

        return (true, null, null);
    }

    // Leads the sixth family of the applications past the tunnel where the machine has a way of its own for it.
    private async Task LaySixAsync((bool Tunnelled, string? Via, string? Device) six, CancellationToken ct)
    {
        if (!six.Tunnelled)
        {
            return;
        }

        if (six.Device is not { } hop)
        {
            _log.Info("apps", "the machine has no way of its own for the sixth family, so it is refused to these applications instead of riding the tunnel");
            return;
        }

        string[][] steps =
        [
            ["-6", "rule", "add", "fwmark", Mark, "lookup", SteeringRules.MainTable, "suppress_prefixlength", "1", "priority", Priority(OwnRulePriority)],
            ["-6", "rule", "add", "fwmark", Mark, "lookup", Table, "priority", Priority(HopRulePriority)],
            SixDefault(six.Via, hop),
        ];
        foreach (var step in steps)
        {
            var (exitCode, output) = await Shell.RunAsync("ip", ct, step).ConfigureAwait(false);
            if (exitCode != 0)
            {
                _log.Warn("apps", $"the sixth family of the applications the list keeps direct rides the tunnel: ip {string.Join(' ', step)} failed: {output}");
                return;
            }
        }

        _log.Info("apps", $"the sixth family of these applications leaves past the tunnel as well, on {hop}");
    }

    // The iproute2 words that lay the default of the sixth family in the table.
    private static string[] SixDefault(string? via, string device)
    {
        return via is null
            ? ["-6", "route", "replace", "default", "dev", device, "table", Table]
            : ["-6", "route", "replace", "default", "via", via, "dev", device, "table", Table];
    }

    // Checks the path back loosely on the device the applications leave by, where the machine checks it strictly.
    private void Loosen()
    {
        try
        {
            var all = int.Parse(File.ReadAllText($"{FilterRoot}/all/rp_filter").Trim(), CultureInfo.InvariantCulture);
            var own = int.Parse(File.ReadAllText($"{FilterRoot}/{_device}/rp_filter").Trim(), CultureInfo.InvariantCulture);
            if (Math.Max(all, own) != 1)
            {
                return;
            }

            File.WriteAllText($"{FilterRoot}/{_device}/rp_filter", "2");
            _strict = own.ToString(CultureInfo.InvariantCulture);
            _log.Info("apps", $"{_device} checks the path back loosely while these applications leave by it: the strict check of this machine drops their answers");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _log.Warn("apps", $"the check of the path back on {_device} was left as it is, so the answers to these applications may be dropped: {ex.Message}");
        }
    }

    // Puts the check of the path back on the device as it stood.
    private void Tighten()
    {
        if (_strict is not { } was)
        {
            return;
        }

        _strict = null;
        try
        {
            File.WriteAllText($"{FilterRoot}/{_device}/rp_filter", was);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("apps", $"the check of the path back on {_device} stays loose: {ex.Message}");
        }
    }

    // Drops the mark, the routing rules and the routing table.
    private static async Task ClearAsync(CancellationToken ct)
    {
        try
        {
            await Shell.RunAsync("nft", ct, "delete", "table", "inet", SteeringRules.DirectTable).ConfigureAwait(false);
            foreach (var family in new[] { "-4", "-6" })
            {
                await DeleteRulesAsync(ct, family, "rule", "del", "fwmark", Mark, "priority", Priority(OwnRulePriority)).ConfigureAwait(false);
                await DeleteRulesAsync(ct, family, "rule", "del", "fwmark", Mark, "priority", Priority(HopRulePriority)).ConfigureAwait(false);
                await Shell.RunAsync("ip", ct, family, "route", "flush", "table", Table).ConfigureAwait(false);
            }
        }
        catch (Win32Exception)
        {
        }
    }

    // Deletes every routing rule of the kind, the ones an earlier run left behind included.
    private static async Task DeleteRulesAsync(CancellationToken ct, params string[] rule)
    {
        var deleted = true;
        while (deleted)
        {
            var (exitCode, _) = await Shell.RunAsync("ip", ct, rule).ConfigureAwait(false);
            deleted = exitCode == 0;
        }
    }

    private static string Priority(int priority) => priority.ToString(CultureInfo.InvariantCulture);
}
