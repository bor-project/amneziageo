using System.Text.Json;

using AmneziaGeo.Decl;

using Microsoft.Data.Sqlite;

namespace AmneziaGeo.Dal;

public sealed class SqliteStateStore(string databasePath) : IStateStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
    }.ToString();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS profiles (
                        name        TEXT PRIMARY KEY,
                        private_key TEXT NOT NULL,
                        public_key  TEXT NOT NULL,
                        endpoint    TEXT NOT NULL,
                        rules_json  TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
    }

    public async Task SaveProfileAsync(TunnelProfile profile, CancellationToken ct = default)
    {
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO profiles (name, private_key, public_key, endpoint, rules_json)
                    VALUES ($name, $priv, $pub, $endpoint, $rules)
                    ON CONFLICT(name) DO UPDATE SET
                        private_key = excluded.private_key,
                        public_key  = excluded.public_key,
                        endpoint    = excluded.endpoint,
                        rules_json  = excluded.rules_json;
                    """;
                command.Parameters.AddWithValue("$name", profile.Name);
                command.Parameters.AddWithValue("$priv", profile.PrivateKey);
                command.Parameters.AddWithValue("$pub", profile.PublicKey);
                command.Parameters.AddWithValue("$endpoint", profile.Endpoint);
                command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(profile.Rules));
                await command.ExecuteNonQueryAsync(ct);
            }
        }
    }

    public async Task<TunnelProfile?> GetProfileAsync(string name, CancellationToken ct = default)
    {
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT private_key, public_key, endpoint, rules_json
                    FROM profiles
                    WHERE name = $name;
                    """;
                command.Parameters.AddWithValue("$name", name);

                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    if (!await reader.ReadAsync(ct))
                    {
                        return null;
                    }

                    var rules = JsonSerializer.Deserialize<List<GeoRule>>(reader.GetString(3)) ?? [];
                    return new TunnelProfile(name, reader.GetString(0), reader.GetString(1), reader.GetString(2), rules);
                }
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListProfileNamesAsync(CancellationToken ct = default)
    {
        var names = new List<string>();

        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM profiles ORDER BY name;";

                await using (var reader = await command.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        names.Add(reader.GetString(0));
                    }
                }
            }
        }

        return names;
    }
}
