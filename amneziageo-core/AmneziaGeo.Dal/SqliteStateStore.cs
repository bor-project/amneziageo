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
                        category_count INTEGER NOT NULL,
                        etag           TEXT NOT NULL DEFAULT '',
                        last_modified  TEXT NOT NULL DEFAULT ''
                    );

                    CREATE TABLE IF NOT EXISTS tunnel_geo (
                        id                INTEGER PRIMARY KEY AUTOINCREMENT,
                        name              TEXT NOT NULL UNIQUE,
                        geo_split         INTEGER NOT NULL,
                        rules_json        TEXT NOT NULL,
                        routes_json       TEXT NOT NULL,
                        domains_json      TEXT NOT NULL,
                        projected         INTEGER NOT NULL DEFAULT 0,
                        proj_split        INTEGER NOT NULL DEFAULT 0,
                        proj_routes_json  TEXT NOT NULL DEFAULT '[]',
                        proj_domains_json TEXT NOT NULL DEFAULT '[]',
                        updated_at        TEXT NOT NULL
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

                    CREATE TABLE IF NOT EXISTS routing_lists (
                        id           INTEGER PRIMARY KEY AUTOINCREMENT,
                        name         TEXT NOT NULL UNIQUE,
                        routes_json  TEXT NOT NULL,
                        domains_json TEXT NOT NULL,
                        updated_at   TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS routing_list_rules (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        list_id    INTEGER NOT NULL REFERENCES routing_lists(id) ON DELETE CASCADE,
                        kind       TEXT NOT NULL,
                        value      TEXT NOT NULL,
                        position   INTEGER NOT NULL,
                        updated_at TEXT NOT NULL,
                        UNIQUE (list_id, position)
                    );
                    """;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await TryAlterAsync(connection, "ALTER TABLE balancers ADD COLUMN mode TEXT NOT NULL DEFAULT 'priority';", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE balancers ADD COLUMN routing_list_id INTEGER;", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE balancers ADD COLUMN use_routing INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);

            // Balancer routing projection lives in its own columns so it never clobbers a config's
            // own set-geo split: the user columns (geo_split/rules/routes/domains) hold user intent,
            // the proj_* columns hold the active projection, and `projected` selects which is live.
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN projected INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN proj_split INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN proj_routes_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN proj_domains_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);

            // HTTP validators for the geo-list update-check: captured at download time so a later check
            // can issue a conditional request and learn whether the remote file changed without re-fetching it.
            await TryAlterAsync(connection, "ALTER TABLE geo_files ADD COLUMN etag TEXT NOT NULL DEFAULT '';", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE geo_files ADD COLUMN last_modified TEXT NOT NULL DEFAULT '';", ct).ConfigureAwait(false);
        }
    }

    private static async Task TryAlterAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        var alter = connection.CreateCommand();
        await using (alter.ConfigureAwait(false))
        {
            alter.CommandText = sql;
            try
            {
                await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
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
    public async Task<TunnelGeo?> GetActiveTunnelGeoAsync(string name, CancellationToken ct = default)
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
                    SELECT projected, geo_split, rules_json, routes_json, domains_json,
                           proj_split, proj_routes_json, proj_domains_json
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

                    // An active balancer projection wins; otherwise fall back to the config's own
                    // set-geo split. The projection carries no rules — the live service only needs
                    // the split flag, routes, and domains.
                    if (reader.GetInt32(0) != 0)
                    {
                        var projRoutes = JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [];
                        var projDomains = JsonSerializer.Deserialize<List<GeoDomain>>(reader.GetString(7)) ?? [];
                        return new TunnelGeo(name, reader.GetInt32(5) != 0, [], projRoutes, projDomains);
                    }

                    var rules = JsonSerializer.Deserialize<List<GeoRule>>(reader.GetString(2)) ?? [];
                    var routes = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [];
                    var domains = JsonSerializer.Deserialize<List<GeoDomain>>(reader.GetString(4)) ?? [];
                    return new TunnelGeo(name, reader.GetInt32(1) != 0, rules, routes, domains);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveTunnelProjectionAsync(string name, bool split, IReadOnlyList<string> routes, IReadOnlyList<GeoDomain> domains, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Insert leaves the user columns at safe defaults (no own split); the conflict path
                // never lists them, so a config's own set-geo split is preserved untouched.
                command.CommandText =
                    """
                    INSERT INTO tunnel_geo (name, geo_split, rules_json, routes_json, domains_json, projected, proj_split, proj_routes_json, proj_domains_json, updated_at)
                    VALUES ($name, 0, '[]', '[]', '[]', 1, $split, $routes, $domains, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        projected         = 1,
                        proj_split        = excluded.proj_split,
                        proj_routes_json  = excluded.proj_routes_json,
                        proj_domains_json = excluded.proj_domains_json,
                        updated_at        = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$split", split ? 1 : 0);
                command.Parameters.AddWithValue("$routes", JsonSerializer.Serialize(routes));
                command.Parameters.AddWithValue("$domains", JsonSerializer.Serialize(domains));
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task ClearTunnelProjectionAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Only the projection is dropped; the user columns are left intact, so the config
                // reverts to its own set-geo split (or no split). A no-op when no row exists.
                command.CommandText =
                    """
                    UPDATE tunnel_geo
                    SET projected         = 0,
                        proj_split        = 0,
                        proj_routes_json  = '[]',
                        proj_domains_json = '[]',
                        updated_at        = $updated
                    WHERE name = $name;
                    """;
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
                // The routing assignment (routing_list_id / use_routing) lives on these same rows.
                // Re-saving a balancer must not silently drop it — losing use_routing would make the
                // next projection clear the members' split and bring the tunnel up full (kill-switch).
                long? routingListId = null;
                var useRouting = false;
                var read = connection.CreateCommand();
                await using (read.ConfigureAwait(false))
                {
                    read.Transaction = transaction;
                    read.CommandText =
                        """
                        SELECT routing_list_id, use_routing
                        FROM balancers
                        WHERE name = $name
                        ORDER BY position
                        LIMIT 1;
                        """;
                    read.Parameters.AddWithValue("$name", balancer.Name);
                    var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            routingListId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                            useRouting = reader.GetInt32(1) != 0;
                        }
                    }
                }

                var delete = connection.CreateCommand();
                await using (delete.ConfigureAwait(false))
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM balancers WHERE name = $name;";
                    delete.Parameters.AddWithValue("$name", balancer.Name);
                    await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // A profile with no members still needs a row: the schema keys a balancer by its member
                // rows (there is no standalone header table), so writing zero rows would make the profile
                // vanish on the next snapshot rebuild — which is exactly why a freshly created "+ Профиль"
                // never appeared. Persist a single placeholder row with an empty-string member instead;
                // GetBalancerAsync filters it back out, so the profile reads as members-empty.
                IReadOnlyList<string> rows = balancer.Members.Count > 0 ? balancer.Members : [string.Empty];
                for (var position = 0; position < rows.Count; position++)
                {
                    var insert = connection.CreateCommand();
                    await using (insert.ConfigureAwait(false))
                    {
                        insert.Transaction = transaction;
                        insert.CommandText =
                            """
                            INSERT INTO balancers (name, position, member, recheck_seconds, mode, routing_list_id, use_routing, updated_at)
                            VALUES ($name, $position, $member, $recheck, $mode, $list, $use, $updated);
                            """;
                        insert.Parameters.AddWithValue("$name", balancer.Name);
                        insert.Parameters.AddWithValue("$position", position);
                        insert.Parameters.AddWithValue("$member", rows[position]);
                        insert.Parameters.AddWithValue("$recheck", balancer.RecheckSeconds);
                        insert.Parameters.AddWithValue("$mode", balancer.Mode);
                        insert.Parameters.AddWithValue("$list", (object?)routingListId ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$use", useRouting ? 1 : 0);
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
                        var member = reader.GetString(0);
                        // Skip the empty-string placeholder row that persists a memberless profile.
                        if (member.Length > 0)
                        {
                            members.Add(member);
                        }

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
                    INSERT INTO geo_files (name, source_url, updated_at, sha256, category_count, etag, last_modified)
                    VALUES ($name, $url, $updated, $sha, $count, $etag, $lastmod)
                    ON CONFLICT(name) DO UPDATE SET
                        source_url     = excluded.source_url,
                        updated_at     = excluded.updated_at,
                        sha256         = excluded.sha256,
                        category_count = excluded.category_count,
                        etag           = excluded.etag,
                        last_modified  = excluded.last_modified;
                    """;
                command.Parameters.AddWithValue("$name", metadata.Name);
                command.Parameters.AddWithValue("$url", metadata.SourceUrl);
                command.Parameters.AddWithValue("$updated", metadata.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$sha", metadata.Sha256);
                command.Parameters.AddWithValue("$count", metadata.CategoryCount);
                command.Parameters.AddWithValue("$etag", metadata.ETag);
                command.Parameters.AddWithValue("$lastmod", metadata.LastModified);
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
                    SELECT source_url, updated_at, sha256, category_count, etag, last_modified
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
                    SELECT name, source_url, updated_at, sha256, category_count, etag, last_modified
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

    /// <inheritdoc/>
    public async Task<long> SaveRoutingListAsync(RoutingList list, CancellationToken ct = default)
    {
        var timestamp = Timestamp();
        var routesJson = JsonSerializer.Serialize(list.Routes);
        var domainsJson = JsonSerializer.Serialize(list.Domains);

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                long id = list.Id;
                if (id == 0)
                {
                    var insert = connection.CreateCommand();
                    await using (insert.ConfigureAwait(false))
                    {
                        insert.Transaction = transaction;
                        insert.CommandText =
                            """
                            INSERT INTO routing_lists (name, routes_json, domains_json, updated_at)
                            VALUES ($name, $routes, $domains, $updated)
                            RETURNING id;
                            """;
                        insert.Parameters.AddWithValue("$name", list.Name);
                        insert.Parameters.AddWithValue("$routes", routesJson);
                        insert.Parameters.AddWithValue("$domains", domainsJson);
                        insert.Parameters.AddWithValue("$updated", timestamp);
                        var scalar = await insert.ExecuteScalarAsync(ct).ConfigureAwait(false);
                        id = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    var update = connection.CreateCommand();
                    await using (update.ConfigureAwait(false))
                    {
                        update.Transaction = transaction;
                        update.CommandText =
                            """
                            UPDATE routing_lists
                            SET name = $name, routes_json = $routes, domains_json = $domains, updated_at = $updated
                            WHERE id = $id;
                            """;
                        update.Parameters.AddWithValue("$id", id);
                        update.Parameters.AddWithValue("$name", list.Name);
                        update.Parameters.AddWithValue("$routes", routesJson);
                        update.Parameters.AddWithValue("$domains", domainsJson);
                        update.Parameters.AddWithValue("$updated", timestamp);
                        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                var deleteRules = connection.CreateCommand();
                await using (deleteRules.ConfigureAwait(false))
                {
                    deleteRules.Transaction = transaction;
                    deleteRules.CommandText = "DELETE FROM routing_list_rules WHERE list_id = $id;";
                    deleteRules.Parameters.AddWithValue("$id", id);
                    await deleteRules.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                for (var position = 0; position < list.Rules.Count; position++)
                {
                    var rule = list.Rules[position];
                    var insertRule = connection.CreateCommand();
                    await using (insertRule.ConfigureAwait(false))
                    {
                        insertRule.Transaction = transaction;
                        insertRule.CommandText =
                            """
                            INSERT INTO routing_list_rules (list_id, kind, value, position, updated_at)
                            VALUES ($list, $kind, $value, $position, $updated);
                            """;
                        insertRule.Parameters.AddWithValue("$list", id);
                        insertRule.Parameters.AddWithValue("$kind", rule.Kind.ToString());
                        insertRule.Parameters.AddWithValue("$value", rule.Value);
                        insertRule.Parameters.AddWithValue("$position", position);
                        insertRule.Parameters.AddWithValue("$updated", timestamp);
                        await insertRule.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return id;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<RoutingList?> GetRoutingListAsync(long id, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return await ReadRoutingListAsync(connection, "id = $key", "$key", id, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<RoutingList?> GetRoutingListByNameAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return await ReadRoutingListAsync(connection, "name = $key", "$key", name, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RoutingList>> ListRoutingListsAsync(CancellationToken ct = default)
    {
        var lists = new List<(long Id, string Name, string Routes, string Domains)>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT id, name, routes_json, domains_json FROM routing_lists ORDER BY name;";
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        lists.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                    }
                }
            }

            var result = new List<RoutingList>(lists.Count);
            foreach (var row in lists)
            {
                var rules = await ReadRoutingListRulesAsync(connection, row.Id, ct).ConfigureAwait(false);
                var routes = JsonSerializer.Deserialize<List<string>>(row.Routes) ?? [];
                var domains = JsonSerializer.Deserialize<List<GeoDomain>>(row.Domains) ?? [];
                result.Add(new RoutingList(row.Id, row.Name, rules, routes, domains));
            }

            return result;
        }
    }

    /// <inheritdoc/>
    public async Task RemoveRoutingListAsync(long id, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var clearAssignments = connection.CreateCommand();
                await using (clearAssignments.ConfigureAwait(false))
                {
                    clearAssignments.Transaction = transaction;
                    clearAssignments.CommandText = "UPDATE balancers SET routing_list_id = NULL, use_routing = 0 WHERE routing_list_id = $id;";
                    clearAssignments.Parameters.AddWithValue("$id", id);
                    await clearAssignments.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var deleteRules = connection.CreateCommand();
                await using (deleteRules.ConfigureAwait(false))
                {
                    deleteRules.Transaction = transaction;
                    deleteRules.CommandText = "DELETE FROM routing_list_rules WHERE list_id = $id;";
                    deleteRules.Parameters.AddWithValue("$id", id);
                    await deleteRules.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var deleteList = connection.CreateCommand();
                await using (deleteList.ConfigureAwait(false))
                {
                    deleteList.Transaction = transaction;
                    deleteList.CommandText = "DELETE FROM routing_lists WHERE id = $id;";
                    deleteList.Parameters.AddWithValue("$id", id);
                    await deleteList.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<(long? RoutingListId, bool UseRouting)> GetProfileRoutingAsync(string profile, CancellationToken ct = default)
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
                    SELECT routing_list_id, use_routing
                    FROM balancers
                    WHERE name = $name
                    ORDER BY position
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$name", profile);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return (null, false);
                    }

                    long? listId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                    var useRouting = reader.GetInt32(1) != 0;
                    return (listId, useRouting);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task SetProfileRoutingAsync(string profile, long? routingListId, bool useRouting, CancellationToken ct = default)
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
                    UPDATE balancers
                    SET routing_list_id = $list, use_routing = $use, updated_at = $updated
                    WHERE name = $name;
                    """;
                command.Parameters.AddWithValue("$name", profile);
                command.Parameters.AddWithValue("$list", (object?)routingListId ?? DBNull.Value);
                command.Parameters.AddWithValue("$use", useRouting ? 1 : 0);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<RoutingList?> ReadRoutingListAsync(SqliteConnection connection, string whereClause, string keyParam, object keyValue, CancellationToken ct)
    {
        long id;
        string name;
        string routesJson;
        string domainsJson;

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"SELECT id, name, routes_json, domains_json FROM routing_lists WHERE {whereClause};";
            command.Parameters.AddWithValue(keyParam, keyValue);
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                id = reader.GetInt64(0);
                name = reader.GetString(1);
                routesJson = reader.GetString(2);
                domainsJson = reader.GetString(3);
            }
        }

        var rules = await ReadRoutingListRulesAsync(connection, id, ct).ConfigureAwait(false);
        var routes = JsonSerializer.Deserialize<List<string>>(routesJson) ?? [];
        var domains = JsonSerializer.Deserialize<List<GeoDomain>>(domainsJson) ?? [];
        return new RoutingList(id, name, rules, routes, domains);
    }

    private static async Task<IReadOnlyList<GeoRule>> ReadRoutingListRulesAsync(SqliteConnection connection, long listId, CancellationToken ct)
    {
        var rules = new List<GeoRule>();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText =
                """
                SELECT kind, value
                FROM routing_list_rules
                WHERE list_id = $id
                ORDER BY position;
                """;
            command.Parameters.AddWithValue("$id", listId);

            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var kind = Enum.Parse<GeoRuleKind>(reader.GetString(0));
                    rules.Add(new GeoRule(kind, reader.GetString(1)));
                }
            }
        }

        return rules;
    }

    private static string Timestamp()
    {
        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private static GeoFileMetadata ReadGeoFile(string name, SqliteDataReader reader, int offset = 0)
    {
        var updatedAt = DateTimeOffset.Parse(reader.GetString(offset + 1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new GeoFileMetadata(
            name,
            reader.GetString(offset),
            updatedAt,
            reader.GetString(offset + 2),
            reader.GetInt32(offset + 3),
            reader.GetString(offset + 4),
            reader.GetString(offset + 5));
    }

    private static BalancerState ReadBalancerState(string group, SqliteDataReader reader, int offset = 0)
    {
        var member = reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1);
        var updatedAt = DateTimeOffset.Parse(reader.GetString(offset + 2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new BalancerState(group, reader.GetString(offset), member, updatedAt);
    }
}
