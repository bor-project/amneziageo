using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The one-time move off named profiles, run against a hand-seeded pre-migration database.
/// </summary>
public sealed class ProfileMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-mig-{Guid.NewGuid():N}.db");
    private SqliteStateStore? _store;

    /// <inheritdoc />
    public void Dispose()
    {
        _store?.ClearPool();
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + ".pre-profiles.bak" })
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task SelectedProfile_HandsItsConfigAndListToTheGlobalSelection()
    {
        Seed(
            "INSERT INTO settings (key, value, updated_at) VALUES ('selected-target', 'work', '2026-01-01');"
            + "INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)"
            + " VALUES ('home', 'nl', 7, 1, '2026-01-02'), ('work', 'de', 42, 1, '2026-01-01');");

        var store = await OpenAsync();

        Assert.Equal("de", await store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.Equal(42, await store.GetSelectedRoutingListAsync());
        Assert.False(TableExists("profiles"));
    }

    [Fact]
    public async Task NoSelection_TakesTheMostRecentlyTouchedProfile()
    {
        Seed(
            "INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)"
            + " VALUES ('old', 'nl', 7, 1, '2026-01-01'), ('fresh', 'de', 42, 1, '2026-02-01');");

        var store = await OpenAsync();

        Assert.Equal("de", await store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.Equal(42, await store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task RoutingOff_LeavesTheGlobalListEmpty()
    {
        Seed(
            "INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)"
            + " VALUES ('work', 'de', 42, 0, '2026-01-01');");

        var store = await OpenAsync();

        Assert.Equal("de", await store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.Null(await store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task EmptyProfileTable_LeavesAConfigSelectionAlone()
    {
        Seed("INSERT INTO settings (key, value, updated_at) VALUES ('selected-target', 'de', '2026-01-01');");

        var store = await OpenAsync();

        Assert.Equal("de", await store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.False(TableExists("profiles"));
    }

    [Fact]
    public async Task Migration_KeepsTheConfigsAndLeavesABackup()
    {
        Seed(
            "INSERT INTO configs (name, config_text, updated_at) VALUES ('de', 'conf', '2026-01-01');"
            + "INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)"
            + " VALUES ('work', 'de', NULL, 0, '2026-01-01');");

        var store = await OpenAsync();

        Assert.True(await store.ConfigExistsAsync("de"));
        Assert.True(File.Exists(_dbPath + ".pre-profiles.bak"));
    }

    // A database as the previous build left it: schema version 1, a profiles table, and the tables the
    // migration reads.
    private void Seed(string rows)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 1;

            CREATE TABLE IF NOT EXISTS settings (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                key        TEXT NOT NULL UNIQUE,
                value      TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS configs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL UNIQUE,
                config_text TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS profiles (
                name            TEXT PRIMARY KEY,
                config          TEXT NOT NULL,
                routing_list_id INTEGER,
                use_routing     INTEGER NOT NULL DEFAULT 0,
                updated_at      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS profile_state (
                name       TEXT PRIMARY KEY,
                status     TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """ + rows;
        command.ExecuteNonQuery();
    }

    private async Task<SqliteStateStore> OpenAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
        return _store;
    }

    private bool TableExists(string name)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
