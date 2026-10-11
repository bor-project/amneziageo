namespace AmneziaGeo.Windows.App;

/// <summary>
/// The file a tunnel keeps its added routes in, read and removed by the tunnel and by the agent at once.
/// </summary>
internal static class RouteStateFile
{
    /// <summary>
    /// How many times a file another process holds is asked for.
    /// </summary>
    public const int Tries = 20;

    private const int PauseMs = 50;
    private const int SettleMs = 5000;

    private static readonly Dictionary<string, Task> _writes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the entries of the file, each once and in the order written.
    /// </summary>
    public static List<string> Read(string path)
    {
        Settle(path);
        return Read(path, File.Exists, File.ReadAllLines, Pause);
    }

    /// <summary>
    /// Adds the entries the file does not hold yet off the calling thread, holding it no longer than the budget.
    /// </summary>
    public static void Add(string path, IReadOnlyCollection<string> entries, int budgetMs, Action<Exception> late)
    {
        Add(path, entries, budgetMs, late, Merge);
    }

    /// <summary>
    /// Adds an entry to the file or takes it back.
    /// </summary>
    public static void Update(string path, string entry, bool add)
    {
        var saved = Read(path);
        if (add)
        {
            if (!saved.Contains(entry))
            {
                saved.Add(entry);
            }
        }
        else
        {
            saved.Remove(entry);
        }

        if (saved.Count == 0)
        {
            Delete(path);
            return;
        }

        Write(path, saved);
    }

    /// <summary>
    /// The record of a route with the adapter and the hop it was put through.
    /// </summary>
    public static string Through(string entry, uint interfaceIndex, string hop)
    {
        return $"{entry} {interfaceIndex} {hop}";
    }

    /// <summary>
    /// Where the route of a record leads.
    /// </summary>
    public static string Entry(string record)
    {
        var space = record.IndexOf(' ');
        return space < 0 ? record : record[..space];
    }

    /// <summary>
    /// The adapter and the hop a record was put through; null for a record written without them.
    /// </summary>
    public static (uint Interface, string Hop)? Hop(string record)
    {
        var parts = record.Split(' ');
        return parts.Length == 3 && uint.TryParse(parts[1], out var index)
            ? (index, parts[2])
            : null;
    }

    /// <summary>
    /// The records the files hold.
    /// </summary>
    public static Held Recorded(IEnumerable<string> files)
    {
        var held = new Held();
        foreach (var file in files)
        {
            held.Add(Read(file));
        }

        return held;
    }

    /// <summary>
    /// The records the other files of the kind hold.
    /// </summary>
    public static Held HeldElsewhere(string own, IEnumerable<string> files)
    {
        var mine = Path.GetFullPath(own);
        return Recorded(files.Where(file => !string.Equals(Path.GetFullPath(file), mine, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Removes the file and says whether it is gone.
    /// </summary>
    public static bool Delete(string path)
    {
        Settle(path);
        return Delete(path, File.Exists, File.Delete, Pause);
    }

    /// <summary>
    /// Reads the entries through the file calls given.
    /// </summary>
    internal static List<string> Read(string path, Func<string, bool> exists, Func<string, string[]> read, Action pause)
    {
        for (var attempt = 1; attempt <= Tries && exists(path); attempt++)
        {
            try
            {
                return Distinct(read(path));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pause();
            }
        }

        return [];
    }

    /// <summary>
    /// Removes the file through the file calls given.
    /// </summary>
    internal static bool Delete(string path, Func<string, bool> exists, Action<string> delete, Action pause)
    {
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            if (!exists(path))
            {
                return true;
            }

            try
            {
                delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pause();
            }
        }

        return !exists(path);
    }

    /// <summary>
    /// Adds the entries with the merge given.
    /// </summary>
    internal static void Add(string path, IReadOnlyCollection<string> entries, int budgetMs, Action<Exception> late, Action<string, IReadOnlyCollection<string>> merge)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var write = Queue(path, () => merge(path, entries));
        if (write.Wait(budgetMs))
        {
            return;
        }

        _ = write.ContinueWith(
            faulted => late(faulted.Exception!.GetBaseException()),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    // Puts the entries the file does not hold yet into it.
    private static void Merge(string path, IReadOnlyCollection<string> entries)
    {
        var saved = Read(path, File.Exists, File.ReadAllLines, Pause);
        var held = new HashSet<string>(saved, StringComparer.Ordinal);
        var changed = false;
        foreach (var entry in entries)
        {
            if (held.Add(entry))
            {
                saved.Add(entry);
                changed = true;
            }
        }

        if (changed)
        {
            Write(path, saved);
        }
    }

    // Writes the entries over the file.
    private static void Write(string path, List<string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllLines(path, entries);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < Tries)
            {
                Pause();
            }
        }
    }

    // Starts a write of the file after the one already on its way.
    private static Task Queue(string path, Action work)
    {
        lock (_writes)
        {
            var write = _writes.TryGetValue(path, out var before) && !before.IsCompleted
                ? before.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                : Task.Run(work);
            _writes[path] = write;
            return write;
        }
    }

    // Waits for the write of the file that is still on its way.
    private static void Settle(string path)
    {
        var write = Pending(path);
        if (write is null)
        {
            return;
        }

        try
        {
            write.Wait(SettleMs);
        }
        catch (AggregateException)
        {
        }

        lock (_writes)
        {
            if (write.IsCompleted && _writes.TryGetValue(path, out var current) && ReferenceEquals(current, write))
            {
                _writes.Remove(path);
            }
        }
    }

    private static Task? Pending(string path)
    {
        lock (_writes)
        {
            return _writes.GetValueOrDefault(path);
        }
    }

    private static List<string> Distinct(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<string>();
        foreach (var line in lines)
        {
            var entry = line.Trim();
            if (entry.Length > 0 && seen.Add(entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static void Pause()
    {
        Thread.Sleep(PauseMs);
    }

    /// <summary>
    /// Records of tunnels, asked whether one of them is of a route.
    /// </summary>
    internal sealed class Held
    {
        private readonly HashSet<string> _records = new(StringComparer.Ordinal);
        private readonly HashSet<string> _entries = new(StringComparer.Ordinal);

        /// <summary>
        /// Takes in the records of one more file.
        /// </summary>
        public void Add(IEnumerable<string> records)
        {
            foreach (var record in records)
            {
                _records.Add(record);
                _entries.Add(Entry(record));
            }
        }

        /// <summary>
        /// Whether a record here is of the same route, or of the same destination when either was written without
        /// its hop.
        /// </summary>
        public bool Holds(string record)
        {
            if (_records.Contains(record))
            {
                return true;
            }

            var entry = Entry(record);
            return entry.Length == record.Length ? _entries.Contains(entry) : _records.Contains(entry);
        }
    }
}
