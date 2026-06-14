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
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        name        TEXT NOT NULL UNIQUE,
                        private_key TEXT NOT NULL,
                        public_key  TEXT NOT NULL,
                        endpoint    TEXT NOT NULL,
                        rules_json  TEXT NOT NULL,
                        updated_at  TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS geo_files (
                        id             INTEGER PRIMARY KEY AUTOINCREMENT,
                        name           TEXT NOT NULL UNIQUE,
                        source_url     TEXT NOT NULL,
                        updated_at     TEXT NOT NULL,
                        sha256         TEXT NOT NULL,
                        category_count INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS tunnel_geo (
                        id           INTEGER PRIMARY KEY AUTOINCREMENT,
                        name         TEXT NOT NULL UNIQUE,
                        geo_split    INTEGER NOT NULL,
                        rules_json   TEXT NOT NULL,
                        routes_json  TEXT NOT NULL,
                        domains_json TEXT NOT NULL,
                        updated_at   TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS domain_ips (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        tunnel     TEXT NOT NULL,
                        domain     TEXT NOT NULL,
                        ip         TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        UNIQUE (tunnel, domain, ip)
                    );

                    CREATE TABLE IF NOT EXISTS geo_sources (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        name       TEXT NOT NULL UNIQUE,
                        kind       TEXT NOT NULL,
                        url        TEXT NOT NULL,
                        position   INTEGER NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS balancers (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        name            TEXT NOT NULL,
                        position        INTEGER NOT NULL,
                        member          TEXT NOT NULL,
                        recheck_seconds INTEGER NOT NULL,
                        mode            TEXT NOT NULL DEFAULT 'priority',
                        updated_at      TEXT NOT NULL,
                        UNIQUE (name, position)
                    );

                    CREATE TABLE IF NOT EXISTS settings (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        key        TEXT NOT NULL UNIQUE,
                        value      TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS balancer_state (
                        id            INTEGER PRIMARY KEY AUTOINCREMENT,
                        group_name    TEXT NOT NULL UNIQUE,
                        status        TEXT NOT NULL,
                        active_member TEXT,
                        updated_at    TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var alter = connection.CreateCommand();
            await using (alter.ConfigureAwait(false))
            {
                alter.CommandText = "ALTER TABLE balancers ADD COLUMN mode TEXT NOT NULL DEFAULT 'priority';";
                try
                {
                    await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (SqliteException)
                {
                }
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
                    INSERT INTO profiles (name, private_key, public_key, endpoint, rules_json, updated_at)
                    VALUES ($name, $priv, $pub, $endpoint, $rules, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        private_key = excluded.private_key,
                        public_key  = excluded.public_key,
                        endpoint    = excluded.endpoint,
                        rules_json  = excluded.rules_json,
                        updated_at  = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", profile.Name);
                command.Parameters.AddWithValue("$priv", profile.PrivateKey);
                command.Parameters.AddWithValue("$pub", profile.PublicKey);
                command.Parameters.AddWithValue("$endpoint", profile.Endpoint);
                command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(profile.Rules));
                command.Parameters.AddWithValue("$updated", Timestamp());
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
                    INSERT INTO tunnel_geo (name, geo_split, rules_json, routes_json, domains_json, updated_at)
                    VALUES ($name, $split, $rules, $routes, $domains, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        geo_split    = excluded.geo_split,
                        rules_json   = excluded.rules_json,
                        routes_json  = excluded.routes_json,
                        domains_json = excluded.domains_json,
                        updated_at   = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", geo.Name);
                command.Parameters.AddWithValue("$split", geo.GeoSplit ? 1 : 0);
                command.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(geo.Rules));
                command.Parameters.AddWithValue("$routes", JsonSerializer.Serialize(geo.Routes));
                command.Parameters.AddWithValue("$domains", JsonSerializer.Serialize(geo.Domains));
                command.Parameters.AddWithValue("$updated", Timestamp());
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
    public async Task RemoveTunnelGeoAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM tunnel_geo WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveGeoSourceAsync(GeoSource source, CancellationToken ct = default)
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
                    INSERT INTO geo_sources (name, kind, url, position, updated_at)
                    VALUES ($name, $kind, $url, $position, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        kind       = excluded.kind,
                        url        = excluded.url,
                        position   = excluded.position,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", source.Name);
                command.Parameters.AddWithValue("$kind", source.Kind);
                command.Parameters.AddWithValue("$url", source.Url);
                command.Parameters.AddWithValue("$position", source.Position);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GeoSource>> ListGeoSourcesAsync(CancellationToken ct = default)
    {
        var sources = new List<GeoSource>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT name, kind, url, position FROM geo_sources ORDER BY position;";

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        sources.Add(new GeoSource(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
                    }
                }
            }
        }

        return sources;
    }

    /// <inheritdoc/>
    public async Task RemoveGeoSourceAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM geo_sources WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveDomainResolutionAsync(string tunnel, DomainResolution resolution, CancellationToken ct = default)
    {
        var timestamp = Timestamp();
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var delete = connection.CreateCommand();
                await using (delete.ConfigureAwait(false))
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM domain_ips WHERE tunnel = $tunnel AND domain = $domain;";
                    delete.Parameters.AddWithValue("$tunnel", tunnel);
                    delete.Parameters.AddWithValue("$domain", resolution.Domain);
                    await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                foreach (var ip in resolution.Ips)
                {
                    var insert = connection.CreateCommand();
                    await using (insert.ConfigureAwait(false))
                    {
                        insert.Transaction = transaction;
                        insert.CommandText =
                            """
                            INSERT INTO domain_ips (tunnel, domain, ip, updated_at)
                            VALUES ($tunnel, $domain, $ip, $updated);
                            """;
                        insert.Parameters.AddWithValue("$tunnel", tunnel);
                        insert.Parameters.AddWithValue("$domain", resolution.Domain);
                        insert.Parameters.AddWithValue("$ip", ip);
                        insert.Parameters.AddWithValue("$updated", timestamp);
                        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DomainResolution>> ListDomainResolutionsAsync(string tunnel, CancellationToken ct = default)
    {
        var resolutions = new List<DomainResolution>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT domain, ip FROM domain_ips WHERE tunnel = $tunnel ORDER BY domain, ip;";
                command.Parameters.AddWithValue("$tunnel", tunnel);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    string? currentDomain = null;
                    var currentIps = new List<string>();
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var domain = reader.GetString(0);
                        if (domain != currentDomain)
                        {
                            if (currentDomain is not null)
                            {
                                resolutions.Add(new DomainResolution(currentDomain, currentIps));
                            }

                            currentDomain = domain;
                            currentIps = [];
                        }

                        currentIps.Add(reader.GetString(1));
                    }

                    if (currentDomain is not null)
                    {
                        resolutions.Add(new DomainResolution(currentDomain, currentIps));
                    }
                }
            }
        }

        return resolutions;
    }

    /// <inheritdoc/>
    public async Task RemoveDomainResolutionsAsync(string tunnel, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM domain_ips WHERE tunnel = $tunnel;";
                command.Parameters.AddWithValue("$tunnel", tunnel);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveBalancerAsync(BalancerGroup balancer, CancellationToken ct = default)
    {
        var timestamp = Timestamp();
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var delete = connection.CreateCommand();
                await using (delete.ConfigureAwait(false))
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM balancers WHERE name = $name;";
                    delete.Parameters.AddWithValue("$name", balancer.Name);
                    await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                for (var position = 0; position < balancer.Members.Count; position++)
                {
                    var insert = connection.CreateCommand();
                    await using (insert.ConfigureAwait(false))
                    {
                        insert.Transaction = transaction;
                        insert.CommandText =
                            """
                            INSERT INTO balancers (name, position, member, recheck_seconds, mode, updated_at)
                            VALUES ($name, $position, $member, $recheck, $mode, $updated);
                            """;
                        insert.Parameters.AddWithValue("$name", balancer.Name);
                        insert.Parameters.AddWithValue("$position", position);
                        insert.Parameters.AddWithValue("$member", balancer.Members[position]);
                        insert.Parameters.AddWithValue("$recheck", balancer.RecheckSeconds);
                        insert.Parameters.AddWithValue("$mode", balancer.Mode);
                        insert.Parameters.AddWithValue("$updated", timestamp);
                        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<BalancerGroup?> GetBalancerAsync(string name, CancellationToken ct = default)
    {
        var members = new List<string>();
        var recheckSeconds = 0;
        var mode = "priority";
        var found = false;

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    SELECT member, recheck_seconds, mode
                    FROM balancers
                    WHERE name = $name
                    ORDER BY position;
                    """;
                command.Parameters.AddWithValue("$name", name);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        members.Add(reader.GetString(0));
                        recheckSeconds = reader.GetInt32(1);
                        mode = reader.GetString(2);
                        found = true;
                    }
                }
            }
        }

        return found ? new BalancerGroup(name, recheckSeconds, members, mode) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListBalancerNamesAsync(CancellationToken ct = default)
    {
        var names = new List<string>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT DISTINCT name FROM balancers ORDER BY name;";

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
    public async Task RemoveBalancerAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM balancers WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
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

    /// <inheritdoc/>
    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT value FROM settings WHERE key = $key;";
                command.Parameters.AddWithValue("$key", key);

                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result as string;
            }
        }
    }

    /// <inheritdoc/>
    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
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
                    INSERT INTO settings (key, value, updated_at)
                    VALUES ($key, $value, $updated)
                    ON CONFLICT(key) DO UPDATE SET
                        value      = excluded.value,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveBalancerStateAsync(BalancerState state, CancellationToken ct = default)
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
                    INSERT INTO balancer_state (group_name, status, active_member, updated_at)
                    VALUES ($group, $status, $member, $updated)
                    ON CONFLICT(group_name) DO UPDATE SET
                        status        = excluded.status,
                        active_member = excluded.active_member,
                        updated_at    = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$group", state.Group);
                command.Parameters.AddWithValue("$status", state.Status);
                command.Parameters.AddWithValue("$member", (object?)state.ActiveMember ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<BalancerState?> GetBalancerStateAsync(string group, CancellationToken ct = default)
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
                    SELECT status, active_member, updated_at
                    FROM balancer_state
                    WHERE group_name = $group;
                    """;
                command.Parameters.AddWithValue("$group", group);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return ReadBalancerState(group, reader);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BalancerState>> ListBalancerStatesAsync(CancellationToken ct = default)
    {
        var states = new List<BalancerState>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    SELECT group_name, status, active_member, updated_at
                    FROM balancer_state
                    ORDER BY group_name;
                    """;

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        states.Add(ReadBalancerState(reader.GetString(0), reader, 1));
                    }
                }
            }
        }

        return states;
    }

    /// <inheritdoc/>
    public async Task BackupToAsync(string destinationPath, CancellationToken ct = default)
    {
        var source = new SqliteConnection(_connectionString);
        await using (source.ConfigureAwait(false))
        {
            await source.OpenAsync(ct).ConfigureAwait(false);

            var checkpoint = source.CreateCommand();
            await using (checkpoint.ConfigureAwait(false))
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Pooling = false,
            }.ToString();
            var destination = new SqliteConnection(destinationConnectionString);
            await using (destination.ConfigureAwait(false))
            {
                await destination.OpenAsync(ct).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }
        }
    }

    /// <inheritdoc/>
    public void ClearPool()
    {
        SqliteConnection.ClearAllPools();
    }

    private static string Timestamp()
    {
        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private static GeoFileMetadata ReadGeoFile(string name, SqliteDataReader reader, int offset = 0)
    {
        var updatedAt = DateTimeOffset.Parse(reader.GetString(offset + 1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new GeoFileMetadata(name, reader.GetString(offset), updatedAt, reader.GetString(offset + 2), reader.GetInt32(offset + 3));
    }

    private static BalancerState ReadBalancerState(string group, SqliteDataReader reader, int offset = 0)
    {
        var member = reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1);
        var updatedAt = DateTimeOffset.Parse(reader.GetString(offset + 2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new BalancerState(group, reader.GetString(offset), member, updatedAt);
    }
}
