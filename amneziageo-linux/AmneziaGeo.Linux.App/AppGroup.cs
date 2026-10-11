using System.Globalization;
using System.Text;

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
    /// Stops placing and hands the processes back to the root cgroup.
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

        Release(Path);
        lock (_placed)
        {
            _placed.Clear();
        }
    }

    /// <summary>
    /// Deletes the cgroup.
    /// </summary>
    public void Remove() => Remove(Path);

    /// <summary>
    /// Hands the processes of a cgroup back to the root one.
    /// </summary>
    public static void Release(string path)
    {
        try
        {
            foreach (var line in File.ReadLines($"{path}/cgroup.procs"))
            {
                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    Place(Root, pid);
                }
            }
        }
        catch (Exception)
        {
        }
    }

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
        if (image is not null && _images.Names(image) && Place(Path, pid))
        {
            _log.Info("apps", $"{image} {_taken}");
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
