using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Carries the named applications through the tunnel and nothing else of theirs past it. The kernel does the
/// steering: the processes live in a cgroup, a netfilter rule marks what leaves it, and the mark selects a routing
/// table whose default is the tunnel. A destination the rules decided keeps its own route in that table, so an
/// address rule outranks an application rule. What stays outside the cgroup is untouched, which is what tells this
/// apart from routing an application's addresses for the whole machine.
/// </summary>
internal sealed class AppTunnel : IDisposable
{
    private const string CgroupRoot = "/sys/fs/cgroup";
    private const string CgroupName = "amneziageo";
    private const string Table = "51820";
    private const string Mark = "0x51820";
    private const string NftTable = "amneziageo";
    private const int RulePriority = 5000;
    private const int SyncIntervalMs = 3000;

    private readonly string _iface;
    private readonly AppImages _images;
    private readonly GeoIpRanges _named;
    private readonly AgentLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<int> _placed = [];
    private Task? _syncing;
    private bool _up;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    private AppTunnel(string interfaceName, AppImages images, GeoIpRanges named, AgentLog log)
    {
        _iface = interfaceName;
        _images = images;
        _named = named;
        _log = log;
    }

    /// <summary>
    /// Path of the cgroup the carried processes live in.
    /// </summary>
    public static string CgroupPath => $"{CgroupRoot}/{CgroupName}";

    /// <summary>
    /// Raises the per-application path for the given rules; null when the rules name no application, the kernel
    /// offers no unified cgroups, or netfilter refuses the mark.
    /// </summary>
    public static async Task<AppTunnel?> TryStartAsync(string interfaceName, IReadOnlyList<string> apps, IReadOnlyList<string> named, AgentLog log, CancellationToken ct)
    {
        var images = AppImages.Parse(apps);
        if (images.Empty)
        {
            return null;
        }

        if (!File.Exists($"{CgroupRoot}/cgroup.controllers"))
        {
            log.Warn("apps", "the kernel offers no unified cgroups here, so the applications keep the path of the machine");
            return null;
        }

        var tunnel = new AppTunnel(interfaceName, images, GeoIpRanges.Build(named), log);
        if (!await tunnel.RaiseAsync(ct).ConfigureAwait(false))
        {
            await tunnel.StopAsync().ConfigureAwait(false);
            return null;
        }

        tunnel._up = true;
        tunnel._syncing = Task.Run(() => tunnel.SyncingAsync(tunnel._cts.Token), CancellationToken.None);
        log.Info("apps", $"{images.Count} application(s) ride {interfaceName} by cgroup, and their traffic alone");
        return tunnel;
    }

    /// <summary>
    /// Puts one destination an address rule named into the table the carried applications route by, so an address
    /// rule outranks the application rules for them as well. A destination no rule named keeps the tunnel, which is
    /// what the application rule asked for.
    /// </summary>
    public void Steer(uint address, string destination, string? via, string? device, bool blocked)
    {
        if (!_up || !_named.Contains(address))
        {
            return;
        }

        var args = Steering(destination, via, device, blocked);
        if (args.Length > 0)
        {
            _ = Shell.RunAsync("ip", CancellationToken.None, args);
        }
    }

    /// <summary>
    /// The address of a host route, in the order the ranges are held in.
    /// </summary>
    public static uint Numeric(IPAddress address) =>
        BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    /// <summary>
    /// Drops one destination from the table the carried applications route by.
    /// </summary>
    public void Unsteer(string destination)
    {
        if (_up)
        {
            _ = Shell.RunAsync("ip", CancellationToken.None, "route", "del", destination, "table", Table);
        }
    }

    /// <summary>
    /// Takes the path down and lets the processes back onto the route of the machine.
    /// </summary>
    public async Task StopAsync()
    {
        _up = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_syncing is not null)
        {
            try
            {
                await _syncing.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Release();
        await Shell.RunAsync("nft", CancellationToken.None, "delete", "table", "inet", NftTable).ConfigureAwait(false);
        await Shell.RunAsync("ip", CancellationToken.None, "rule", "del", "fwmark", Mark, "lookup", Table).ConfigureAwait(false);
        await Shell.RunAsync("ip", CancellationToken.None, "-6", "rule", "del", "fwmark", Mark, "lookup", Table).ConfigureAwait(false);
        await Shell.RunAsync("ip", CancellationToken.None, "route", "flush", "table", Table).ConfigureAwait(false);
        await Shell.RunAsync("ip", CancellationToken.None, "-6", "route", "flush", "table", Table).ConfigureAwait(false);
        try
        {
            Directory.Delete(CgroupPath);
        }
        catch (Exception)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    // The iproute2 words that put one destination into the application table.
    private static string[] Steering(string destination, string? via, string? device, bool blocked)
    {
        if (blocked)
        {
            return ["route", "replace", "blackhole", destination, "table", Table];
        }

        if (via is { Length: > 0 } && device is { Length: > 0 })
        {
            return ["route", "replace", destination, "via", via, "dev", device, "table", Table];
        }

        return device is { Length: > 0 }
            ? ["route", "replace", destination, "dev", device, "table", Table]
            : [];
    }

    // Builds the cgroup, the mark and the table the mark selects.
    private async Task<bool> RaiseAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(CgroupPath);
        }
        catch (Exception ex)
        {
            _log.Warn("apps", $"the cgroup could not be made: {ex.Message}");
            return false;
        }

        // What a killed run left behind would send the mark at a table that routes nowhere.
        await Shell.RunAsync("ip", ct, "rule", "del", "fwmark", Mark, "lookup", Table).ConfigureAwait(false);
        await Shell.RunAsync("ip", ct, "-6", "rule", "del", "fwmark", Mark, "lookup", Table).ConfigureAwait(false);
        await Shell.RunAsync("ip", ct, "route", "flush", "table", Table).ConfigureAwait(false);
        await Shell.RunAsync("ip", ct, "-6", "route", "flush", "table", Table).ConfigureAwait(false);
        await Shell.RunAsync("nft", ct, "delete", "table", "inet", NftTable).ConfigureAwait(false);

        if (!await MarkAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        var rule = await Shell.RunAsync("ip", ct, "rule", "add", "fwmark", Mark, "lookup", Table,
            "priority", RulePriority.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (rule.ExitCode != 0)
        {
            _log.Warn("apps", $"the routing rule was refused: {rule.Output}");
            return false;
        }

        await CarveAsync(ct, "-4").ConfigureAwait(false);
        var route = await Shell.RunAsync("ip", ct, "route", "replace", "default", "dev", _iface, "table", Table).ConfigureAwait(false);
        if (route.ExitCode != 0)
        {
            _log.Warn("apps", $"the default of the application table was refused: {route.Output}");
            return false;
        }

        await SixAsync(ct).ConfigureAwait(false);
        return true;
    }

    // The sixth family takes the same mark, so it needs the same table: the tunnel where the tunnel carries it, and
    // a wall where it does not. Left alone it would walk out the physical link while the fourth rides the tunnel.
    private async Task SixAsync(CancellationToken ct)
    {
        var rule = await Shell.RunAsync("ip", ct, "-6", "rule", "add", "fwmark", Mark, "lookup", Table,
            "priority", RulePriority.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (rule.ExitCode != 0)
        {
            _log.Warn("apps", $"the sixth family keeps the path of the machine: {rule.Output}");
            return;
        }

        await CarveAsync(ct, "-6").ConfigureAwait(false);
        var carried = await Shell.RunAsync("ip", ct, "-6", "address", "show", "dev", _iface, "scope", "global").ConfigureAwait(false);
        var tunnelled = carried.ExitCode == 0 && carried.Output.Contains("inet6", StringComparison.Ordinal);
        var route = tunnelled
            ? await Shell.RunAsync("ip", ct, "-6", "route", "replace", "default", "dev", _iface, "table", Table).ConfigureAwait(false)
            : await Shell.RunAsync("ip", ct, "-6", "route", "replace", "unreachable", "default", "table", Table).ConfigureAwait(false);
        if (route.ExitCode != 0)
        {
            _log.Warn("apps", $"the sixth family of the application table was refused: {route.Output}");
            return;
        }

        _log.Info("apps", tunnelled
            ? "the sixth family of the carried applications rides the tunnel as well"
            : "the tunnel carries no sixth family, so it is refused to the carried applications instead of leaking");
    }

    // Marks what leaves the cgroup and rewrites its source on the way out: the address the socket took belongs to
    // the physical link, and the peer answers only the address the tunnel carries.
    private async Task<bool> MarkAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "amneziageo-apps.nft");
        var text = string.Join('\n',
            "table inet " + NftTable + " {",
            "  chain apps {",
            "    type route hook output priority mangle; policy accept;",
            "    socket cgroupv2 level 1 \"" + CgroupName + "\" meta mark set " + Mark,
            "  }",
            "  chain source {",
            "    type nat hook postrouting priority srcnat; policy accept;",
            "    meta mark " + Mark + " oifname \"" + _iface + "\" masquerade",
            "  }",
            "}",
            string.Empty);
        try
        {
            await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
            var applied = await Shell.RunAsync("nft", ct, "-f", path).ConfigureAwait(false);
            if (applied.ExitCode != 0)
            {
                _log.Warn("apps", $"netfilter refused the mark: {applied.Output}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("apps", $"the mark could not be written: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }

    // Copies what the machine reaches without a default into the application table, or the carried processes lose
    // the local networks the moment their default becomes the tunnel.
    private async Task CarveAsync(CancellationToken ct, string family)
    {
        var (code, output) = await Shell.RunAsync("ip", ct, family, "route", "show", "table", "main").ConfigureAwait(false);
        if (code != 0)
        {
            return;
        }
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("default", StringComparison.Ordinal) || line.Contains(_iface, StringComparison.Ordinal))
            {
                continue;
            }

            var args = new List<string> { family, "route", "replace" };
            args.AddRange(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            args.Add("table");
            args.Add(Table);
            await Shell.RunAsync("ip", ct, [.. args]).ConfigureAwait(false);
        }
    }

    // Keeps the cgroup filled: a process that starts later is carried from the next pass on, and its children come
    // with it on their own.
    private async Task SyncingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Fill();
                await Task.Delay(SyncIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("apps", $"filling the cgroup failed: {ex.Message}");
            }
        }
    }

    // Puts every process the rules name into the cgroup.
    private void Fill()
    {
        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            var leaf = Path.GetFileName(entry);
            if (!int.TryParse(leaf, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || _placed.Contains(pid))
            {
                continue;
            }

            var image = Image(pid);
            if (image is null || !_images.Names(image))
            {
                continue;
            }

            if (Place(CgroupPath, pid))
            {
                _placed.Add(pid);
                _log.Info("apps", $"{image} rides the tunnel from now on");
            }
        }
    }

    // The executable a pid runs, or null when it is gone or unreadable.
    private static string? Image(int pid)
    {
        try
        {
            var link = new FileInfo($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/exe").LinkTarget;
            return link is { Length: > 0 } ? link : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Moves one process into the cgroup; its threads and children follow it there. The file is opened as it stands:
    // cgroupfs takes one line and refuses a write that asks to truncate it first.
    private static bool Place(string cgroup, int pid)
    {
        try
        {
            using var stream = new FileStream($"{cgroup}/cgroup.procs", FileMode.Open, FileAccess.Write);
            stream.Write(Encoding.ASCII.GetBytes(pid.ToString(CultureInfo.InvariantCulture) + "\n"));
            stream.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Hands the carried processes back to the root cgroup, so they keep running on the route of the machine.
    private void Release()
    {
        try
        {
            foreach (var line in File.ReadLines($"{CgroupPath}/cgroup.procs"))
            {
                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    Place(CgroupRoot, pid);
                }
            }
        }
        catch (Exception)
        {
        }

        _placed.Clear();
    }
}
