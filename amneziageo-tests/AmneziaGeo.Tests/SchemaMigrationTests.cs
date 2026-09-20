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
    public async Task InitializeAsync_TurnsAWebSocketPortNobodyChoseToThePortOfTheEndpointOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-schema-ws-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteStateStore(path);
            await store.InitializeAsync();
            await store.SetConfigTransportAsync(new ConfigTransport("untouched", false, string.Empty, 443));
            await store.SetConfigTransportAsync(new ConfigTransport("on", true, string.Empty, 443));
            await store.SetConfigTransportAsync(new ConfigTransport("named", false, "front.example", 443));
            await ForgetAsync(path, "schema-legacy-ws-port-cleared");

            var again = new SqliteStateStore(path);
            await again.InitializeAsync();
            await again.SetConfigTransportAsync(new ConfigTransport("chosen", false, string.Empty, 443));
            await new SqliteStateStore(path).InitializeAsync();

            Assert.Equal(0, (await again.GetConfigTransportAsync("untouched"))?.WebSocketPort);
            Assert.Equal(443, (await again.GetConfigTransportAsync("on"))?.WebSocketPort);
            Assert.Equal(443, (await again.GetConfigTransportAsync("named"))?.WebSocketPort);
            Assert.Equal(443, (await again.GetConfigTransportAsync("chosen"))?.WebSocketPort);
        }
        finally
        {
            Cleanup(path);
        }
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
