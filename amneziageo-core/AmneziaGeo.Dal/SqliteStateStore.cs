using System.Globalization;
using System.Text.Json;

using AmneziaGeo.Decl;

using Microsoft.Data.Sqlite;

namespace AmneziaGeo.Dal;

/// <summary>
/// SQLite-backed <see cref="IStateStore"/>.
/// </summary>
public sealed class SqliteStateStore(string databasePath) : IStateStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
    }.ToString();

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
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

                    CREATE TABLE IF NOT EXISTS geo_files (
                        name           TEXT PRIMARY KEY,
                        source_url     TEXT NOT NULL,
                        updated_at     TEXT NOT NULL,
                        sha256         TEXT NOT NULL,
                        category_count INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS tunnel_geo (
                        name         TEXT PRIMARY KEY,
                        geo_split    INTEGER NOT NULL,
                        rules_json   TEXT NOT NULL,
                        routes_json  TEXT NOT NULL,
                        domains_json TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveProfileAsync(TunnelProfile profile, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
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
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<TunnelProfile?> GetProfileAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    SELECT private_key, public_key, endpoint, rules_json
                    FROM profiles
                    WHERE name = $name;
                    """;
                command.Parameters.AddWithValue("$name", name);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var rules = JsonSerializer.Deserialize<List<GeoRule>>(reader.GetString(3)) ?? [];
                    return new TunnelProfile(name, reader.GetString(0), reader.GetString(1), reader.GetString(2), rules);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListProfileNamesAsync(CancellationToken ct = default)
    {
        var names = new List<string>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT name FROM profiles ORDER BY name;";

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        names.Add(reader.GetString(0));
                    }
                }
            }
        }

        return names;
    }

    /// <inheritdoc/>
    public async Task SaveTunnelGeoAsync(TunnelGeo geo, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    INSERT INTO tunnel_geo (name, geo_split, rules_json, routes_json, domains_json)
                    VALUES ($name, $split, $rules, $routes, $domains)
                    ON CONFLICT(name) DO UPDATE SET
                        geo_split    = excluded.geo_split,
                        rules_json   = excluded.rules_json,
                        routes_json  = excluded.routes_json,
                        domains_json = excluded.domains_json;
                    """;
                command.Parameters.AddWithValue("$name", geo.Name);
                command.Parameters.AddWithValue("$split", geo.GeoSplit ? 1 : 0);
                command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(geo.Rules));
                command.Parameters.AddWithValue("$routes", JsonSerializer.Serialize(geo.Routes));
                command.Parameters.AddWithValue("$domains", JsonSerializer.Serialize(geo.Domains));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<TunnelGeo?> GetTunnelGeoAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    SELECT geo_split, rules_json, routes_json, domains_json
                    FROM tunnel_geo
                    WHERE name = $name;
                    """;
                command.Parameters.AddWithValue("$name", name);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var rules = JsonSerializer.Deserialize<List<GeoRule>>(reader.GetString(1)) ?? [];
                    var routes = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [];
                    var domains = JsonSerializer.Deserialize<List<GeoDomain>>(reader.GetString(3)) ?? [];
                    return new TunnelGeo(name, reader.GetInt32(0) != 0, rules, routes, domains);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListTunnelGeoNamesAsync(CancellationToken ct = default)
    {
        var names = new List<string>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT name FROM tunnel_geo ORDER BY name;";

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        names.Add(reader.GetString(0));
                    }
                }
            }
        }

        return names;
    }

    /// <inheritdoc/>
    public async Task SaveGeoFileAsync(GeoFileMetadata metadata, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    INSERT INTO geo_files (name, source_url, updated_at, sha256, category_count)
                    VALUES ($name, $url, $updated, $sha, $count)
                    ON CONFLICT(name) DO UPDATE SET
                        source_url     = excluded.source_url,
                        updated_at     = excluded.updated_at,
                        sha256         = excluded.sha256,
                        category_count = excluded.category_count;
                    """;
                command.Parameters.AddWithValue("$name", metadata.Name);
                command.Parameters.AddWithValue("$url", metadata.SourceUrl);
                command.Parameters.AddWithValue("$updated", metadata.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$sha", metadata.Sha256);
                command.Parameters.AddWithValue("$count", metadata.CategoryCount);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<GeoFileMetadata?> GetGeoFileAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    SELECT source_url, updated_at, sha256, category_count
                    FROM geo_files
                    WHERE name = $name;
                    """;
                command.Parameters.AddWithValue("$name", name);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return ReadGeoFile(name, reader);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GeoFileMetadata>> ListGeoFilesAsync(CancellationToken ct = default)
    {
        var files = new List<GeoFileMetadata>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    SELECT name, source_url, updated_at, sha256, category_count
                    FROM geo_files
                    ORDER BY name;
                    """;

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        files.Add(ReadGeoFile(reader.GetString(0), reader, 1));
                    }
                }
            }
        }

        return files;
    }

    private static GeoFileMetadata ReadGeoFile(string name, SqliteDataReader reader, int offset = 0)
    {
        var updatedAt = DateTimeOffset.Parse(reader.GetString(offset + 1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new GeoFileMetadata(name, reader.GetString(offset), updatedAt, reader.GetString(offset + 2), reader.GetInt32(offset + 3));
    }
}
