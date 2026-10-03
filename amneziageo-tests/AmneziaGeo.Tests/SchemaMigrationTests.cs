using System.Runtime.ExceptionServices;

using AmneziaGeo.Dal;
using AmneziaGeo.Decl;

using Microsoft.Data.Sqlite;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Schema migration of the state database. The column list is applied on every start, so a file that already
/// carries it must be left alone: running the statements and letting SQLite refuse each one stops a debugger
/// two dozen times at every launch, and hides a real failure among the refusals.
/// </summary>
public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task InitializeAsync_OnADatabaseAlreadyMigrated_AddsNothingTwice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-schema-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteStateStore(path).InitializeAsync();

            var refused = 0;
            void Count(object? sender, FirstChanceExceptionEventArgs e)
            {
                if (e.Exception is SqliteException
                    && e.Exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref refused);
                }
            }

            AppDomain.CurrentDomain.FirstChanceException += Count;
            try
            {
                await new SqliteStateStore(path).InitializeAsync();
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= Count;
            }

            Assert.Equal(0, refused);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task InitializeAsync_OnADatabaseMissingAColumn_AddsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-schema-old-{Guid.NewGuid():N}.db");
        try
        {
            await WriteLegacyTransportAsync(path);

            await new SqliteStateStore(path).InitializeAsync();

            Assert.Contains("use_router", await ColumnsAsync(path, "config_transport"));
            Assert.Contains("mtu_mode", await ColumnsAsync(path, "config_transport"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task InitializeAsync_OnAStoreOpenedAgain_KeepsTheAutoModeBesideAStoredSize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-schema-mode-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteStateStore(path);
            await store.InitializeAsync();
            await store.SetConfigTransportAsync(new ConfigTransport("office", false, 1420, MtuMode: MtuMode.Auto));

            var again = new SqliteStateStore(path);
            await again.InitializeAsync();

            var transport = await again.GetConfigTransportAsync("office");
            Assert.Equal(MtuMode.Auto, transport?.MtuMode);
            Assert.Equal(1420, transport?.Mtu);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task InitializeAsync_OnARowFromBeforeTheModes_TurnsItsSizeIntoACustomOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-schema-sized-{Guid.NewGuid():N}.db");
        try
        {
            await WriteSizedTransportAsync(path, "office", 1400);

            var store = new SqliteStateStore(path);
            await store.InitializeAsync();

            var transport = await store.GetConfigTransportAsync("office");
            Assert.Equal(MtuMode.Custom, transport?.MtuMode);
            Assert.Equal(1400, transport?.Mtu);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task InitializeAsync_OnASizeBelowTheSmallestOne_RaisesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-schema-small-{Guid.NewGuid():N}.db");
        try
        {
            await WriteSizedTransportAsync(path, "office", 1200);

            var store = new SqliteStateStore(path);
            await store.InitializeAsync();

            var transport = await store.GetConfigTransportAsync("office");
            Assert.Equal(MtuMode.Custom, transport?.MtuMode);
            Assert.Equal(1280, transport?.Mtu);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task SetConfigTransportAsync_OnASizeBelowTheSmallestOne_StoresTheSmallest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-schema-floor-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteStateStore(path);
            await store.InitializeAsync();
            await store.SetConfigTransportAsync(new ConfigTransport("office", false, 1200, MtuMode: MtuMode.Custom));
            await store.SetConfigTransportAsync(new ConfigTransport("home", false, 0, MtuMode: MtuMode.Custom));

            Assert.Equal(1280, (await store.GetConfigTransportAsync("office"))?.Mtu);
            Assert.Equal(0, (await store.GetConfigTransportAsync("home"))?.Mtu);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // A config_transport from before the modes, with one row that carries a size.
    private static async Task WriteSizedTransportAsync(string path, string name, int mtu)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    CREATE TABLE config_transport (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        name       TEXT NOT NULL UNIQUE,
                        use_ws     INTEGER NOT NULL DEFAULT 0,
                        ws_port    INTEGER NOT NULL DEFAULT 443,
                        mtu        INTEGER NOT NULL DEFAULT 0,
                        updated_at TEXT NOT NULL
                    );
                    INSERT INTO config_transport (name, mtu, updated_at) VALUES ($name, $mtu, '2026-01-01T00:00:00Z');
                    PRAGMA user_version = 1;
                    """;
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$mtu", mtu);
                await command.ExecuteNonQueryAsync();
            }
        }

        ClearPool(path);
    }

    // Drops a one-time marker, as a file written before the rewrite carries none.
    private static async Task ForgetAsync(string path, string key)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM settings WHERE key = $key;";
                command.Parameters.AddWithValue("$key", key);
                await command.ExecuteNonQueryAsync();
            }
        }

        ClearPool(path);
    }

    // A config_transport from before the transport columns, under the current schema version so the store
    // migrates it instead of dropping it.
    private static async Task WriteLegacyTransportAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    CREATE TABLE config_transport (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        name       TEXT NOT NULL UNIQUE,
                        use_ws     INTEGER NOT NULL DEFAULT 0,
                        ws_port    INTEGER NOT NULL DEFAULT 443,
                        updated_at TEXT NOT NULL
                    );
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }
        }

        ClearPool(path);
    }

    private static async Task<List<string>> ColumnsAsync(string path, string table)
    {
        var columns = new List<string>();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
                var reader = await command.ExecuteReaderAsync();
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync())
                    {
                        columns.Add(reader.GetString(0));
                    }
                }
            }
        }

        return columns;
    }

    // Drops the pooled connections to this test's database alone.
    private static void ClearPool(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private static void Cleanup(string path)
    {
        ClearPool(path);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
