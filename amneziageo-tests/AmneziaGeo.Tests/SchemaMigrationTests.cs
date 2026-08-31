using System.Runtime.ExceptionServices;

using AmneziaGeo.Dal;

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

        SqliteConnection.ClearAllPools();
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

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
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
