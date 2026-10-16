using AmneziaGeo.Dal;

using Microsoft.Data.Sqlite;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The window, the tray and the setup keep running after they read their preferences: between calls the store holds
/// nothing open in the state database, so the setup's reset can remove the folder while the setup still runs.
/// </summary>
public sealed class LocalKeyValueStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ageo-prefs-{Guid.NewGuid():N}");

    /// <summary>
    /// ctor
    /// </summary>
    public LocalKeyValueStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    private string DbPath => Path.Combine(_dir, "state.db");

    /// <inheritdoc />
    public void Dispose()
    {
        ClearPool();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [WindowsFact("an open file blocks its removal only on Windows")]
    public void LoadAndSave_LeaveTheFolderRemovable()
    {
        var store = new LocalKeyValueStore(DbPath);
        store.Save("installer", new Dictionary<string, string>(StringComparer.Ordinal) { ["launch-after"] = "false" });
        Assert.Equal("false", store.Load("installer")["launch-after"]);

        Directory.Delete(_dir, recursive: true);

        Assert.False(Directory.Exists(_dir));
    }

    [WindowsFact("an open file blocks its removal only on Windows")]
    public void Read_LeavesTheFolderRemovable()
    {
        new LocalKeyValueStore(DbPath).Save("ui", new Dictionary<string, string>(StringComparer.Ordinal) { ["language"] = "en" });
        ClearPool();

        Assert.Equal("en", new LocalKeyValueStore(DbPath).Read("ui", "language"));

        Directory.Delete(_dir, recursive: true);

        Assert.False(Directory.Exists(_dir));
    }

    // Drops the pooled connections to the test database.
    private void ClearPool()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        SqliteConnection.ClearPool(connection);
    }
}
