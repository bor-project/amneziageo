namespace AmneziaGeo.Dal;

/// <summary>
/// Moves a corrupt SQLite database and its WAL sidecars aside so the store can recreate a clean one. A stale
/// -wal/-shm pair from a killed process corrupts recovery of an otherwise intact file, so the sidecars are
/// quarantined together with the database.
/// </summary>
public static class CorruptQuarantine
{
    /// <summary>
    /// Renames only the -wal and -shm sidecars to *.corrupt-&lt;stamp&gt;, keeping the main file - a stale
    /// sidecar pair is the common corruption and the database's own data survives it.
    /// </summary>
    public static void MoveAsideSidecars(string databasePath)
    {
        MoveAll([databasePath + "-wal", databasePath + "-shm"]);
    }

    /// <summary>
    /// Renames the database, -wal and -shm files to *.corrupt-&lt;stamp&gt; next to the originals.
    /// </summary>
    public static void MoveAside(string databasePath)
    {
        MoveAll([databasePath, databasePath + "-wal", databasePath + "-shm"]);
    }

    private static void MoveAll(IReadOnlyList<string> paths)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var path in paths)
        {
            MoveWithRetry(path, $"{path}.corrupt-{stamp}");
        }
    }

    // A dying sibling process may briefly hold the file; ride out the window.
    private static void MoveWithRetry(string source, string target)
    {
        for (var attempt = 0; attempt < 5 && File.Exists(source); attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
