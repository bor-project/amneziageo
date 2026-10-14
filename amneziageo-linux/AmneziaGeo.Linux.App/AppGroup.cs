using System.Globalization;
using System.Text;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Keeps the processes that run the named images in a cgroup of the agent: the ones running already and the ones
/// that start later.
/// </summary>
internal sealed class AppGroup : IDisposable
{
    /// <summary>
    /// The root of the unified cgroups.
    /// </summary>
    public const string Root = "/sys/fs/cgroup";

    private const int SyncIntervalMs = 3000;

    private readonly AppImages _images;
    private readonly string _taken;
    private readonly AgentLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<int> _placed = [];
    private readonly Dictionary<int, string> _homes = [];
    private Task? _syncing;
    private ProcEvents? _events;
    private volatile bool _watching;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public AppGroup(string name, AppImages images, string taken, AgentLog log)
    {
        Path = PathOf(name);
        _images = images;
        _taken = taken;
        _log = log;
    }

    /// <summary>
    /// Whether the kernel offers the unified cgroups.
    /// </summary>
    public static bool Offered => File.Exists($"{Root}/cgroup.controllers");

    /// <summary>
    /// Path of the cgroup.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Path of a cgroup of the agent by its name.
    /// </summary>
    public static string PathOf(string name) => $"{Root}/{name}";

    /// <summary>
    /// Makes the cgroup; false when the system refuses it.
    /// </summary>
    public bool Make()
    {
        try
        {
            Directory.CreateDirectory(Path);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("apps", $"the cgroup could not be made: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts placing the processes the images name.
    /// </summary>
    public void Watch()
    {
        _watching = true;
        _syncing = Task.Run(() => SyncingAsync(_cts.Token), CancellationToken.None);
        _events = ProcEvents.TryListen(Carry, _log);
    }

    /// <summary>
    /// Stops placing and hands each process back to the cgroup it was taken from.
    /// </summary>
    public async Task StopAsync()
    {
        _watching = false;
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

        var homes = Homes();
        Release(Path, pid => CgroupHomes.Back(pid, homes, Parent) ?? Root);
        lock (_placed)
        {
            _placed.Clear();
            _homes.Clear();
        }
    }

    /// <summary>
    /// Deletes the cgroup.
    /// </summary>
    public void Remove() => Remove(Path);

    /// <summary>
    /// Hands the processes of a cgroup back to the root one.
    /// </summary>
    public static void Release(string path) => Release(path, _ => Root);

    /// <summary>
    /// Deletes a cgroup that holds no process.
    /// </summary>
    public static void Remove(string path)
    {
        try
        {
            Directory.Delete(path);
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

    // Hands each process of a cgroup to the cgroup named for it, or to the root one when that one does not take it.
    private static void Release(string path, Func<int, string> back)
    {
        try
        {
            foreach (var line in File.ReadAllLines($"{path}/cgroup.procs"))
            {
                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                    && !Place(back(pid), pid))
                {
                    Place(Root, pid);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    // Fills the cgroup with what the connector did not report.
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

    // Puts every process the images name into the cgroup.
    private void Fill()
    {
        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(System.IO.Path.GetFileName(entry), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                Carry(pid);
            }
        }
    }

    // Puts one process into the cgroup when the images name the one it runs.
    private void Carry(int pid)
    {
        if (!_watching)
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
        if (image is not null && _images.Names(image))
        {
            var home = Home(pid);
            if (Place(Path, pid))
            {
                Keep(pid, home);
                _log.Info("apps", $"{image} {_taken}");
                return;
            }
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

    // The cgroup a pid lives in, or null when it is gone or unreadable.
    private static string? Home(int pid)
    {
        try
        {
            var path = CgroupHomes.Of(File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/cgroup"));

            return path is null ? null : Root + path.TrimEnd('/');
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The parent of a pid, or null when it is gone or unreadable.
    private static int? Parent(int pid)
    {
        try
        {
            return CgroupHomes.Parent(File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Keeps the cgroup a placed pid was taken from.
    private void Keep(int pid, string? home)
    {
        if (home is null || home == Path)
        {
            return;
        }

        lock (_placed)
        {
            _homes[pid] = home;
        }
    }

    // A copy of the cgroups the placed pids were taken from.
    private Dictionary<int, string> Homes()
    {
        lock (_placed)
        {
            return new Dictionary<int, string>(_homes);
        }
    }

    // Moves one process into a cgroup; its threads and children follow it there.
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
}
