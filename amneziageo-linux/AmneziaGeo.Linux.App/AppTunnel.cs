using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Carries the named applications through the tunnel and nothing else of theirs past it, every outbound datagram
/// of the machine where all UDP is asked for, and whatever the machine opens toward the ranges a list sends
/// through the tunnel. The kernel does the steering: the processes live in a cgroup, a netfilter rule marks what
/// leaves it, and the mark selects a routing table whose default is the tunnel. A destination the rules decided
/// keeps its own route in that table, so an address rule outranks an application rule, and the ranges a list
/// keeps direct take no mark at all. What stays outside the cgroup is untouched, which is what tells this apart
/// from routing an application's addresses for the whole machine.
/// </summary>
internal sealed class AppTunnel : IDisposable
{
    private const string CgroupRoot = "/sys/fs/cgroup";
    private const string CgroupName = "amneziageo";
    private const string Table = "51820";
    private const string Mark = SteeringRules.Mark;
    private const string NftTable = SteeringRules.Table;
    private const int RulePriority = 5000;
    private const int SyncIntervalMs = 3000;

    private readonly string _iface;
    private readonly AppImages _images;
    private readonly GeoIpRanges _named;
    private readonly IReadOnlyList<string>? _ranges;
    private readonly IReadOnlyList<string>? _past;
    private readonly bool _allUdp;
    private readonly string? _endpoint;
    private readonly AgentLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<int> _placed = [];
    private readonly HashSet<uint> _spared = [];
    private GeoIpRanges _carried;
    private GeoIpRanges _passed;
    private Task? _syncing;
    private ProcEvents? _events;
    private bool _up;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    private AppTunnel(string interfaceName, AppImages images, GeoIpRanges named, IReadOnlyList<string>? ranges, IEnumerable<uint> spared, IReadOnlyList<string> past, bool allUdp, string? endpoint, AgentLog log)
    {
        _iface = interfaceName;
        _images = images;
        _named = named;
        _ranges = ranges;
        _carried = GeoIpRanges.Build(ranges ?? []);
        _spared.UnionWith(spared.Where(_carried.Contains));
        // The mark of the ranges leaves the direct ones out by itself; the other two take them unless told.
        _past = images.Empty && !allUdp ? null : past;
        _passed = GeoIpRanges.Build(_past ?? []);
        _allUdp = allUdp;
        _endpoint = endpoint;
        _log = log;
    }

    /// <summary>
    /// Path of the cgroup the carried processes live in.
    /// </summary>
    public static string CgroupPath => $"{CgroupRoot}/{CgroupName}";

    /// <summary>
    /// Raises the per-application path for the given rules, the path of the ranges a list sends through the tunnel
    /// and the datagram path where all UDP is asked for; null when none is asked for, the kernel offers no unified
    /// cgroups, or netfilter refuses the mark. Null ranges stand for a tunnel that carries everything; the spared
    /// addresses are the ones a name already holds off the tunnel; the ranges past it are the ones the list keeps
    /// direct, which neither an application nor a datagram is carried to.
    /// </summary>
    public static async Task<AppTunnel?> TryStartAsync(string interfaceName, IReadOnlyList<string> apps, IReadOnlyList<string> named, IReadOnlyList<string>? ranges, IEnumerable<uint> spared, IReadOnlyList<string> past, bool allUdp, string? endpoint, AgentLog log, CancellationToken ct)
    {
        var images = AppImages.Parse(apps);
        if (images.Empty && !allUdp && ranges is not { Count: > 0 })
        {
            return null;
        }

        if (!images.Empty && !File.Exists($"{CgroupRoot}/cgroup.controllers"))
        {
            log.Warn("apps", "the kernel offers no unified cgroups here, so the applications keep the path of the machine");
            return null;
        }

        var tunnel = new AppTunnel(interfaceName, images, GeoIpRanges.Build(named), ranges, spared, past, allUdp, endpoint, log);
        if (!await tunnel.RaiseAsync(ct).ConfigureAwait(false))
        {
            await tunnel.StopAsync().ConfigureAwait(false);
            return null;
        }

        tunnel._up = true;
        if (!images.Empty)
        {
            tunnel._syncing = Task.Run(() => tunnel.SyncingAsync(tunnel._cts.Token), CancellationToken.None);
            tunnel._events = ProcEvents.TryListen(tunnel.Carry, log);
            log.Info("apps", $"{images.Count} application(s) ride {interfaceName} by cgroup, and their traffic alone");
        }

        if (allUdp)
        {
            log.Info("apps", $"every outbound datagram rides {interfaceName}, apart from the local networks and the server itself");
        }

        if (ranges is { Count: > 0 })
        {
            log.Info("routing", $"what this machine opens toward the {ranges.Count} tunnel range(s) of the list rides {interfaceName} from the first packet");
        }

        if (tunnel._past is { Count: > 0 } direct)
        {
            log.Info("routing", $"neither an application nor a datagram is carried to the {direct.Count} direct range(s) of the list");
        }

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
    /// Replaces the ranges of the list the marks go by: the tunnel ones, sparing in the same go the held addresses
    /// they now cover, and the ones past the tunnel. True when the tunnel ones were laid anew; false when this
    /// path stands no set of them or netfilter refuses.
    /// </summary>
    public bool Reload(IReadOnlyList<string> ranges, IEnumerable<uint> held, IReadOnlyList<string> past)
    {
        if (!_up || (_ranges is null && _past is null))
        {
            return false;
        }

        var direct = _past is null ? null : past;
        var passed = GeoIpRanges.Build(direct ?? []);
        if (_ranges is null)
        {
            lock (_spared)
            {
                if (Load(SteeringRules.Reload(null, [], direct)))
                {
                    _passed = passed;
                }
            }

            return false;
        }

        var carried = GeoIpRanges.Build(ranges);
        lock (_spared)
        {
            var fresh = held.Where(address => carried.Contains(address) && !_spared.Contains(address)).Distinct().ToList();
            if (!Load(SteeringRules.Reload(ranges, [.. fresh.Select(GeoIpRanges.Format)], direct)))
            {
                return false;
            }

            _spared.UnionWith(fresh);
            _carried = carried;
            _passed = passed;
        }

        return true;
    }

    /// <summary>
    /// Whether the mark of the ranges carries the address: a range of the list covers it and no name spares it.
    /// </summary>
    public bool Carries(uint address)
    {
        if (!_up || _ranges is null)
        {
            return false;
        }

        lock (_spared)
        {
            return _carried.Contains(address) && !_spared.Contains(address);
        }
    }

    /// <summary>
    /// Keeps one address off the marks: one a tunnel range of the list covers, or one a rule keeps direct where
    /// an application or every datagram is marked; false when no mark would take it.
    /// </summary>
    public bool Spare(uint address, bool direct)
    {
        if (!_up || (_ranges is null && _past is null))
        {
            return false;
        }

        lock (_spared)
        {
            if (!_carried.Contains(address) && !(direct && _past is not null && !_passed.Contains(address)))
            {
                if (_spared.Remove(address))
                {
                    Element("delete", address);
                }

                return false;
            }

            if (!_spared.Add(address))
            {
                return true;
            }

            if (Element("add", address))
            {
                return true;
            }

            _spared.Remove(address);
            return false;
        }
    }

    /// <summary>
    /// Hands one address back to the marks.
    /// </summary>
    public void Unspare(uint address)
    {
        if (!_up || (_ranges is null && _past is null))
        {
            return;
        }

        lock (_spared)
        {
            if (_spared.Remove(address))
            {
                Element("delete", address);
            }
        }
    }

    /// <summary>
    /// Takes the path down and lets the processes back onto the route of the machine.
    /// </summary>
    public async Task StopAsync()
    {
        _up = false;
        _events?.Dispose();
        _events = null;
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
        _events?.Dispose();
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
        if (!_images.Empty)
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

        Loosen();

        // The ranges of a list are of the fourth family alone.
        if (!_images.Empty || _allUdp)
        {
            await SixAsync(ct).ConfigureAwait(false);
        }

        return true;
    }

    // Takes the answers to what the mark steered where the machine checks the path back strictly.
    private void Loosen()
    {
        const string Root = "/proc/sys/net/ipv4/conf";
        try
        {
            var all = int.Parse(File.ReadAllText($"{Root}/all/rp_filter").Trim(), CultureInfo.InvariantCulture);
            var own = int.Parse(File.ReadAllText($"{Root}/{_iface}/rp_filter").Trim(), CultureInfo.InvariantCulture);
            if (Math.Max(all, own) != 1)
            {
                return;
            }

            File.WriteAllText($"{Root}/{_iface}/rp_filter", "2");
            _log.Info("apps", $"{_iface} checks the path back loosely: the strict check of this machine drops the answers to what the mark steered");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _log.Warn("apps", $"the check of the path back on {_iface} was left as it is: {ex.Message}");
        }
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

    // Marks what the path carries and rewrites its source on the way out: the address the socket took belongs to
    // the physical link, and the peer answers only the address the tunnel carries.
    private async Task<bool> MarkAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "amneziageo-apps.nft");
        var text = SteeringRules.Marks(_iface, _images.Empty ? null : CgroupName, _ranges, _allUdp, _endpoint,
            [.. _spared.Select(GeoIpRanges.Format)], _past);
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

    // Hands netfilter the lines in one go.
    private bool Load(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "amneziageo-ranges.nft");
        try
        {
            File.WriteAllText(path, text);
            var (code, output) = Shell.RunAsync("nft", CancellationToken.None, "-f", path).GetAwaiter().GetResult();
            if (code != 0)
            {
                _log.Warn("routing", $"netfilter refused the ranges of the list: {output}");
            }

            return code == 0;
        }
        catch (Exception ex)
        {
            _log.Warn("routing", $"the ranges of the list could not be written: {ex.Message}");
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

    // Adds one address to the spared ones or deletes it from them.
    private bool Element(string verb, uint address)
    {
        var host = GeoIpRanges.Format(address);
        try
        {
            var (code, output) = Shell.RunAsync("nft", CancellationToken.None, verb, "element", "inet", NftTable,
                SteeringRules.Spared, "{", host, "}").GetAwaiter().GetResult();
            if (code != 0)
            {
                _log.Warn("routing", $"netfilter refused to {verb} {host} among the spared addresses: {output}");
            }

            return code == 0;
        }
        catch (Exception ex)
        {
            _log.Warn("routing", $"netfilter could not {verb} {host} among the spared addresses: {ex.Message}");
            return false;
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

    // Keeps the cgroup filled with what the connector did not report: a process the pass finds is carried from then
    // on, and its children come with it on their own.
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
            if (int.TryParse(Path.GetFileName(entry), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                Carry(pid);
            }
        }
    }

    // Puts one process into the cgroup when the rules name the image it runs.
    private void Carry(int pid)
    {
        if (!_up)
        {
            return;
        }

        lock (_placed)
        {
            if (!_placed.Add(pid))
            {
                return;
            }
        }

        var image = Image(pid);
        if (image is not null && _images.Names(image) && Place(CgroupPath, pid))
        {
            _log.Info("apps", $"{image} rides the tunnel from now on");
            return;
        }

        lock (_placed)
        {
            _placed.Remove(pid);
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

        lock (_placed)
        {
            _placed.Clear();
        }
    }
}
