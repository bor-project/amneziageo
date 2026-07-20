using Microsoft.Data.Sqlite;

namespace AmneziaGeo.Dal;

/// <summary>
/// Per-scope key/value rows in a private table of the state database, usable from any process without
/// the schema-version reset of <see cref="SqliteStateStore"/>. Reserved for UI/installer preferences.
/// </summary>
public sealed class LocalKeyValueStore(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
    }.ToString();

    /// <summary>
    /// Reads every key/value pair stored under a scope.
    /// </summary>
    public IReadOnlyDictionary<string, string> Load(string scope)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using var connection = Open();
        EnsureTable(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM local_prefs WHERE scope = $scope;";
        command.Parameters.AddWithValue("$scope", scope);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    /// <summary>
    /// Upserts every key/value pair under a scope in one transaction.
    /// </summary>
    public void Save(string scope, IReadOnlyDictionary<string, string> values)
    {
        using var connection = Open();
        EnsureTable(connection);

        using var transaction = connection.BeginTransaction();
        foreach (var (key, value) in values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO local_prefs (scope, key, value) VALUES ($scope, $key, $value)
                ON CONFLICT(scope, key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS local_prefs (
                scope TEXT NOT NULL,
                key   TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (scope, key)
            );
            """;
        command.ExecuteNonQuery();
    }
}
