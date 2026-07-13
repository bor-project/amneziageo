using System.Globalization;
using System.Text.Json;

using AmneziaGeo.Decl;

using Microsoft.Data.Sqlite;

namespace AmneziaGeo.Dal;

/// <summary>
/// SQLite-backed state store.
/// </summary>
public sealed class SqliteStateStore(string databasePath) : IStateStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
    }.ToString();

    private const int SchemaVersion = 1;

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // WAL is persisted in the DB header (set once): the 1-5s domain poll reader and on-demand hydration
            // no longer block on the writer's lock, and BackupToAsync's wal_checkpoint(TRUNCATE) becomes
            // meaningful. Safe multi-process on the local ProgramData disk shared by the agent and each tunnel.
            var walCommand = connection.CreateCommand();
            await using (walCommand.ConfigureAwait(false))
            {
                walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                await walCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var schemaVersion = await ReadUserVersionAsync(connection, ct).ConfigureAwait(false);
            if (schemaVersion != SchemaVersion)
            {
                // Prior or unversioned schema: reset to the clean normalized schema (#163, no legacy migration).
                await DropAllTablesAsync(connection, ct).ConfigureAwait(false);
            }

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
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
                        proj_apps_json    TEXT NOT NULL DEFAULT '[]',
                        proj_routing_list_id INTEGER,
                        updated_at        TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS config_transport (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        name       TEXT NOT NULL UNIQUE,
                        use_ws     INTEGER NOT NULL DEFAULT 0,
                        ws_host    TEXT NOT NULL DEFAULT '',
                        ws_port    INTEGER NOT NULL DEFAULT 443,
                        mtu        INTEGER NOT NULL DEFAULT 1280,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS config_dns (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        name       TEXT NOT NULL UNIQUE,
                        servers    TEXT NOT NULL DEFAULT '',
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS config_exclusions (
                        id               INTEGER PRIMARY KEY AUTOINCREMENT,
                        name             TEXT NOT NULL UNIQUE,
                        exclusions       TEXT NOT NULL DEFAULT '',
                        auto_exclude_lan INTEGER NOT NULL DEFAULT 1,
                        updated_at       TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS configs (
                        name       TEXT PRIMARY KEY,
                        text       TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
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

                    CREATE TABLE IF NOT EXISTS profiles (
                        name            TEXT PRIMARY KEY,
                        config          TEXT NOT NULL,
                        routing_list_id INTEGER,
                        use_routing     INTEGER NOT NULL DEFAULT 0,
                        updated_at      TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS settings (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        key        TEXT NOT NULL UNIQUE,
                        value      TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS profile_state (
                        name       TEXT PRIMARY KEY,
                        status     TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS routing_lists (
                        id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                        name                TEXT NOT NULL UNIQUE,
                        routes_json         TEXT NOT NULL,
                        domains_json        TEXT NOT NULL,
                        direct_routes_json  TEXT NOT NULL DEFAULT '[]',
                        direct_domains_json TEXT NOT NULL DEFAULT '[]',
                        block_routes_json   TEXT NOT NULL DEFAULT '[]',
                        block_domains_json  TEXT NOT NULL DEFAULT '[]',
                        updated_at          TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS routing_list_rules (
                        id         INTEGER PRIMARY KEY AUTOINCREMENT,
                        list_id    INTEGER NOT NULL REFERENCES routing_lists(id) ON DELETE CASCADE,
                        kind       TEXT NOT NULL,
                        value      TEXT NOT NULL,
                        role       TEXT NOT NULL DEFAULT 'Proxy',
                        position   INTEGER NOT NULL,
                        updated_at TEXT NOT NULL,
                        UNIQUE (list_id, position)
                    );

                    CREATE TABLE IF NOT EXISTS routing_settings (
                        list_id          INTEGER PRIMARY KEY REFERENCES routing_lists(id) ON DELETE CASCADE,
                        exclusions       TEXT NOT NULL DEFAULT '',
                        all_udp          INTEGER NOT NULL DEFAULT 0,
                        mode             TEXT NOT NULL DEFAULT 'split',
                        use_ipv6         INTEGER NOT NULL DEFAULT 0,
                        use_global_proxy INTEGER NOT NULL DEFAULT 0,
                        updated_at       TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Projection columns hold the live profile routing; user columns hold config intent.
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN projected INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN proj_split INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN proj_routes_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN proj_domains_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);
            // Materialized app matchers for the projection (config path derives apps from rules).
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN proj_apps_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);

            // Routing list a live projection came from (null for full-tunnel / no-list).
            await TryAlterAsync(connection, "ALTER TABLE tunnel_geo ADD COLUMN proj_routing_list_id INTEGER;", ct).ConfigureAwait(false);

            // HTTP validators for conditional update-checks.
            await TryAlterAsync(connection, "ALTER TABLE geo_files ADD COLUMN etag TEXT NOT NULL DEFAULT '';", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE geo_files ADD COLUMN last_modified TEXT NOT NULL DEFAULT '';", ct).ConfigureAwait(false);

            // WebSocket transport host.
            await TryAlterAsync(connection, "ALTER TABLE config_transport ADD COLUMN ws_host TEXT NOT NULL DEFAULT '';", ct).ConfigureAwait(false);

            // Tunnel MTU (default 1280, valid 576-1500). A stored 1380 (the former default) is treated as
            // "follow the current default" at connect time (TunnelRunner.LegacyDefaultMtu), so existing
            // default-valued configs pick up the lowered MTU without clobbering an explicit user choice.
            await TryAlterAsync(connection, "ALTER TABLE config_transport ADD COLUMN mtu INTEGER NOT NULL DEFAULT 1280;", ct).ConfigureAwait(false);

            // Generation counter, bumped when the materialized set changes.
            await TryAlterAsync(connection, "ALTER TABLE routing_lists ADD COLUMN generation INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);

            // Routing list a cached resolution belongs to (0 = none/unknown); lets a list's cache be cleaned on removal.
            await TryAlterAsync(connection, "ALTER TABLE domain_ips ADD COLUMN list_id INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);

            // Per-list IPv6 opt-in (#149); off keeps the tunnel v4-only.
            await TryAlterAsync(connection, "ALTER TABLE routing_settings ADD COLUMN use_ipv6 INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);

            // Global-proxy opt-in: full tunnel minus the Direct bucket; off tunnels only the Proxy bucket.
            await TryAlterAsync(connection, "ALTER TABLE routing_settings ADD COLUMN use_global_proxy INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);

            // Per-rule role (Proxy/Direct/Block); existing rows default to Proxy.
            await TryAlterAsync(connection, "ALTER TABLE routing_list_rules ADD COLUMN role TEXT NOT NULL DEFAULT 'Proxy';", ct).ConfigureAwait(false);

            // Materialized Direct/Block buckets alongside the Proxy routes_json/domains_json.
            await TryAlterAsync(connection, "ALTER TABLE routing_lists ADD COLUMN direct_routes_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE routing_lists ADD COLUMN direct_domains_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE routing_lists ADD COLUMN block_routes_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);
            await TryAlterAsync(connection, "ALTER TABLE routing_lists ADD COLUMN block_domains_json TEXT NOT NULL DEFAULT '[]';", ct).ConfigureAwait(false);

            await SetUserVersionAsync(connection, ct).ConfigureAwait(false);
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

    private static async Task<long> ReadUserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "PRAGMA user_version;";
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is long version ? version : 0;
        }
    }

    private static async Task SetUserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task DropAllTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var tables = new List<string>();
        var select = connection.CreateCommand();
        await using (select.ConfigureAwait(false))
        {
            select.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tables.Add(reader.GetString(0));
                }
            }
        }

        foreach (var table in tables)
        {
            var drop = connection.CreateCommand();
            await using (drop.ConfigureAwait(false))
            {
                drop.CommandText = $"DROP TABLE IF EXISTS \"{table}\";";
                await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
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
                    return new TunnelGeo(name, reader.GetInt32(0) != 0, rules, routes, domains, ExtractApps(rules));
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
                           proj_split, proj_routes_json, proj_domains_json, proj_apps_json
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

                    // Active profile projection wins; otherwise the config's own split. Projections carry no rules.
                    if (reader.GetInt32(0) != 0)
                    {
                        var projRoutes = JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [];
                        var projDomains = JsonSerializer.Deserialize<List<GeoDomain>>(reader.GetString(7)) ?? [];
                        var projApps = JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? [];
                        return new TunnelGeo(name, reader.GetInt32(5) != 0, [], projRoutes, projDomains, projApps);
                    }

                    var rules = JsonSerializer.Deserialize<List<GeoRule>>(reader.GetString(2)) ?? [];
                    var routes = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [];
                    var domains = JsonSerializer.Deserialize<List<GeoDomain>>(reader.GetString(4)) ?? [];
                    return new TunnelGeo(name, reader.GetInt32(1) != 0, rules, routes, domains, ExtractApps(rules));
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveTunnelProjectionAsync(string name, bool split, IReadOnlyList<string> routes, IReadOnlyList<GeoDomain> domains, IReadOnlyList<string> apps, long? routingListId, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Insert keeps user columns at defaults; conflict path preserves the config's own split.
                command.CommandText =
                    """
                    INSERT INTO tunnel_geo (name, geo_split, rules_json, routes_json, domains_json, projected, proj_split, proj_routes_json, proj_domains_json, proj_apps_json, proj_routing_list_id, updated_at)
                    VALUES ($name, 0, '[]', '[]', '[]', 1, $split, $routes, $domains, $apps, $list, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        projected            = 1,
                        proj_split           = excluded.proj_split,
                        proj_routes_json     = excluded.proj_routes_json,
                        proj_domains_json    = excluded.proj_domains_json,
                        proj_apps_json       = excluded.proj_apps_json,
                        proj_routing_list_id = excluded.proj_routing_list_id,
                        updated_at           = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$split", split ? 1 : 0);
                command.Parameters.AddWithValue("$routes", JsonSerializer.Serialize(routes));
                command.Parameters.AddWithValue("$domains", JsonSerializer.Serialize(domains));
                command.Parameters.AddWithValue("$apps", JsonSerializer.Serialize(apps));
                command.Parameters.AddWithValue("$list", (object?)routingListId ?? DBNull.Value);
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
                // Drop the projection; config reverts to its own split. No-op when no row.
                command.CommandText =
                    """
                    UPDATE tunnel_geo
                    SET projected            = 0,
                        proj_split           = 0,
                        proj_routes_json     = '[]',
                        proj_domains_json    = '[]',
                        proj_apps_json       = '[]',
                        proj_routing_list_id = NULL,
                        updated_at           = $updated
                    WHERE name = $name;
                    """;
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<long?> GetActiveRoutingListIdAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Only a live projection names a routing list; null = full-tunnel / no-list.
                command.CommandText = "SELECT proj_routing_list_id FROM tunnel_geo WHERE name = $name AND projected = 1;";
                command.Parameters.AddWithValue("$name", name);

                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
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
    public async Task<ConfigTransport?> GetConfigTransportAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT use_ws, ws_host, ws_port, mtu FROM config_transport WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new ConfigTransport(name, reader.GetInt32(0) != 0, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3));
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task SetConfigTransportAsync(ConfigTransport transport, CancellationToken ct = default)
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
                    INSERT INTO config_transport (name, use_ws, ws_host, ws_port, mtu, updated_at)
                    VALUES ($name, $use, $host, $port, $mtu, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        use_ws     = excluded.use_ws,
                        ws_host    = excluded.ws_host,
                        ws_port    = excluded.ws_port,
                        mtu        = excluded.mtu,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", transport.Name);
                command.Parameters.AddWithValue("$use", transport.UseWebSocket ? 1 : 0);
                command.Parameters.AddWithValue("$host", transport.WebSocketHost);
                command.Parameters.AddWithValue("$port", transport.WebSocketPort);
                command.Parameters.AddWithValue("$mtu", transport.Mtu);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task RemoveConfigTransportAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM config_transport WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<ConfigDns?> GetConfigDnsAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT servers FROM config_dns WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new ConfigDns(name, reader.GetString(0));
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task SetConfigDnsAsync(ConfigDns dns, CancellationToken ct = default)
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
                    INSERT INTO config_dns (name, servers, updated_at)
                    VALUES ($name, $servers, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        servers    = excluded.servers,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", dns.Name);
                command.Parameters.AddWithValue("$servers", dns.Servers);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task RemoveConfigDnsAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM config_dns WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<ConfigExclusions?> GetConfigExclusionsAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT exclusions FROM config_exclusions WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new ConfigExclusions(name, reader.GetString(0));
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task SetConfigExclusionsAsync(ConfigExclusions exclusions, CancellationToken ct = default)
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
                    INSERT INTO config_exclusions (name, exclusions, updated_at)
                    VALUES ($name, $exclusions, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        exclusions = excluded.exclusions,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", exclusions.Name);
                command.Parameters.AddWithValue("$exclusions", exclusions.Exclusions);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task RemoveConfigExclusionsAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM config_exclusions WHERE name = $name;";
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
    public async Task SaveDomainResolutionAsync(string tunnel, DomainResolution resolution, long listId, CancellationToken ct = default)
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
                            INSERT INTO domain_ips (tunnel, list_id, domain, ip, updated_at)
                            VALUES ($tunnel, $list, $domain, $ip, $updated);
                            """;
                        insert.Parameters.AddWithValue("$tunnel", tunnel);
                        insert.Parameters.AddWithValue("$list", listId);
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
                // DISTINCT (domain, ip) across list ids: one /32 per domain even if several lists cache it.
                command.CommandText = "SELECT domain, ip, MAX(updated_at) FROM domain_ips WHERE tunnel = $tunnel GROUP BY domain, ip ORDER BY domain, ip;";
                command.Parameters.AddWithValue("$tunnel", tunnel);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    string? currentDomain = null;
                    var currentIps = new List<string>();
                    DateTimeOffset? currentResolvedAt = null;
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var domain = reader.GetString(0);
                        if (domain != currentDomain)
                        {
                            if (currentDomain is not null)
                            {
                                resolutions.Add(new DomainResolution(currentDomain, currentIps, currentResolvedAt));
                            }

                            currentDomain = domain;
                            currentIps = [];
                            currentResolvedAt = null;
                        }

                        currentIps.Add(reader.GetString(1));
                        // Resolved-at is the freshest write across the domain's IP rows.
                        var rowResolvedAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                        if (currentResolvedAt is null || rowResolvedAt > currentResolvedAt)
                        {
                            currentResolvedAt = rowResolvedAt;
                        }
                    }

                    if (currentDomain is not null)
                    {
                        resolutions.Add(new DomainResolution(currentDomain, currentIps, currentResolvedAt));
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
    public async Task DeleteDomainResolutionAsync(string tunnel, string domain, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM domain_ips WHERE tunnel = $tunnel AND domain = $domain;";
                command.Parameters.AddWithValue("$tunnel", tunnel);
                command.Parameters.AddWithValue("$domain", domain);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<DomainResolution?> GetDomainResolutionAsync(string tunnel, string domain, CancellationToken ct = default)
    {
        var ips = new List<string>();
        DateTimeOffset? resolvedAt = null;

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Point lookup served by the (tunnel, domain) prefix of the UNIQUE(tunnel, domain, ip) index.
                command.CommandText = "SELECT ip, updated_at FROM domain_ips WHERE tunnel = $tunnel AND domain = $domain;";
                command.Parameters.AddWithValue("$tunnel", tunnel);
                command.Parameters.AddWithValue("$domain", domain);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        ips.Add(reader.GetString(0));
                        // Resolved-at is the freshest write across the domain's IP rows.
                        var rowResolvedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                        if (resolvedAt is null || rowResolvedAt > resolvedAt)
                        {
                            resolvedAt = rowResolvedAt;
                        }
                    }
                }
            }
        }

        return ips.Count > 0 ? new DomainResolution(domain, ips, resolvedAt) : null;
    }

    /// <inheritdoc/>
    public async Task SaveProfileAsync(Profile profile, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Preserve the routing assignment across re-saves; only config/updated_at move.
                command.CommandText =
                    """
                    INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)
                    VALUES ($name, $config, NULL, 0, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        config     = excluded.config,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", profile.Name);
                command.Parameters.AddWithValue("$config", profile.Config);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<Profile?> GetProfileAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT config FROM profiles WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return new Profile(name, reader.GetString(0));
                    }
                }
            }
        }

        return null;
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
    public async Task RemoveProfileAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM profiles WHERE name = $name;";
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
    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT key, value FROM settings;";

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        map[reader.GetString(0)] = reader.GetString(1);
                    }
                }
            }
        }

        return map;
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
    public async Task<bool> ConfigExistsAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT 1 FROM configs WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);
                return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetConfigTextAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT text FROM configs WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);
                return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListConfigNamesAsync(CancellationToken ct = default)
    {
        var names = new List<string>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT name FROM configs ORDER BY name;";

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
    public async Task SaveConfigAsync(string name, string text, CancellationToken ct = default)
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
                    INSERT INTO configs (name, text, created_at, updated_at)
                    VALUES ($name, $text, $now, $now)
                    ON CONFLICT(name) DO UPDATE SET
                        text       = excluded.text,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$text", text);
                command.Parameters.AddWithValue("$now", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task RenameConfigAsync(string oldName, string newName, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "UPDATE configs SET name = $new, updated_at = $now WHERE name = $old;";
                command.Parameters.AddWithValue("$new", newName);
                command.Parameters.AddWithValue("$old", oldName);
                command.Parameters.AddWithValue("$now", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task RemoveConfigAsync(string name, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM configs WHERE name = $name;";
                command.Parameters.AddWithValue("$name", name);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task SaveProfileStateAsync(ProfileState state, CancellationToken ct = default)
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
                    INSERT INTO profile_state (name, status, updated_at)
                    VALUES ($name, $status, $updated)
                    ON CONFLICT(name) DO UPDATE SET
                        status     = excluded.status,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$name", state.Name);
                command.Parameters.AddWithValue("$status", state.Status);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<ProfileState?> GetProfileStateAsync(string name, CancellationToken ct = default)
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
                    SELECT status, updated_at
                    FROM profile_state
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

                    return ReadProfileState(name, reader);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProfileState>> ListProfileStatesAsync(CancellationToken ct = default)
    {
        var states = new List<ProfileState>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    SELECT name, status, updated_at
                    FROM profile_state
                    ORDER BY name;
                    """;

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        states.Add(ReadProfileState(reader.GetString(0), reader, 1));
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
        var directRoutesJson = JsonSerializer.Serialize(list.DirectRoutes);
        var directDomainsJson = JsonSerializer.Serialize(list.DirectDomains);
        var blockRoutesJson = JsonSerializer.Serialize(list.BlockRoutes);
        var blockDomainsJson = JsonSerializer.Serialize(list.BlockDomains);

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
                            INSERT INTO routing_lists (name, routes_json, domains_json, direct_routes_json, direct_domains_json, block_routes_json, block_domains_json, generation, updated_at)
                            VALUES ($name, $routes, $domains, $directRoutes, $directDomains, $blockRoutes, $blockDomains, 1, $updated)
                            RETURNING id;
                            """;
                        insert.Parameters.AddWithValue("$name", list.Name);
                        insert.Parameters.AddWithValue("$routes", routesJson);
                        insert.Parameters.AddWithValue("$domains", domainsJson);
                        insert.Parameters.AddWithValue("$directRoutes", directRoutesJson);
                        insert.Parameters.AddWithValue("$directDomains", directDomainsJson);
                        insert.Parameters.AddWithValue("$blockRoutes", blockRoutesJson);
                        insert.Parameters.AddWithValue("$blockDomains", blockDomainsJson);
                        insert.Parameters.AddWithValue("$updated", timestamp);
                        var scalar = await insert.ExecuteScalarAsync(ct).ConfigureAwait(false);
                        id = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    // Bump generation only when the materialized set changes (byte-equal JSON = unchanged).
                    long generation = 1;
                    var current = connection.CreateCommand();
                    await using (current.ConfigureAwait(false))
                    {
                        current.Transaction = transaction;
                        current.CommandText = "SELECT routes_json, domains_json, direct_routes_json, direct_domains_json, block_routes_json, block_domains_json, generation FROM routing_lists WHERE id = $id;";
                        current.Parameters.AddWithValue("$id", id);
                        var reader = await current.ExecuteReaderAsync(ct).ConfigureAwait(false);
                        await using (reader.ConfigureAwait(false))
                        {
                            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                            {
                                var unchanged = reader.GetString(0) == routesJson
                                    && reader.GetString(1) == domainsJson
                                    && reader.GetString(2) == directRoutesJson
                                    && reader.GetString(3) == directDomainsJson
                                    && reader.GetString(4) == blockRoutesJson
                                    && reader.GetString(5) == blockDomainsJson;
                                var oldGeneration = reader.GetInt64(6);
                                generation = unchanged ? oldGeneration : oldGeneration + 1;
                            }
                        }
                    }

                    var update = connection.CreateCommand();
                    await using (update.ConfigureAwait(false))
                    {
                        update.Transaction = transaction;
                        update.CommandText =
                            """
                            UPDATE routing_lists
                            SET name = $name, routes_json = $routes, domains_json = $domains,
                                direct_routes_json = $directRoutes, direct_domains_json = $directDomains,
                                block_routes_json = $blockRoutes, block_domains_json = $blockDomains,
                                generation = $generation, updated_at = $updated
                            WHERE id = $id;
                            """;
                        update.Parameters.AddWithValue("$id", id);
                        update.Parameters.AddWithValue("$name", list.Name);
                        update.Parameters.AddWithValue("$routes", routesJson);
                        update.Parameters.AddWithValue("$domains", domainsJson);
                        update.Parameters.AddWithValue("$directRoutes", directRoutesJson);
                        update.Parameters.AddWithValue("$directDomains", directDomainsJson);
                        update.Parameters.AddWithValue("$blockRoutes", blockRoutesJson);
                        update.Parameters.AddWithValue("$blockDomains", blockDomainsJson);
                        update.Parameters.AddWithValue("$generation", generation);
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
                            INSERT INTO routing_list_rules (list_id, kind, value, role, position, updated_at)
                            VALUES ($list, $kind, $value, $role, $position, $updated);
                            """;
                        insertRule.Parameters.AddWithValue("$list", id);
                        insertRule.Parameters.AddWithValue("$kind", rule.Kind.ToString());
                        insertRule.Parameters.AddWithValue("$value", rule.Value);
                        insertRule.Parameters.AddWithValue("$role", rule.Role.ToString());
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
        var lists = new List<(long Id, string Name, string Routes, string Domains, string DirectRoutes, string DirectDomains, string BlockRoutes, string BlockDomains)>();

        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    SELECT id, name, routes_json, domains_json,
                           direct_routes_json, direct_domains_json, block_routes_json, block_domains_json
                    FROM routing_lists ORDER BY name;
                    """;
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        lists.Add((reader.GetInt64(0), reader.GetString(1),
                            reader.GetString(2), reader.GetString(3),
                            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
                    }
                }
            }

            var result = new List<RoutingList>(lists.Count);
            foreach (var row in lists)
            {
                var rules = await ReadRoutingListRulesAsync(connection, row.Id, ct).ConfigureAwait(false);
                result.Add(BuildRoutingList(row.Id, row.Name, rules,
                    row.Routes, row.Domains, row.DirectRoutes, row.DirectDomains, row.BlockRoutes, row.BlockDomains));
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
                    clearAssignments.CommandText = "UPDATE profiles SET routing_list_id = NULL, use_routing = 0 WHERE routing_list_id = $id;";
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

                var deleteSettings = connection.CreateCommand();
                await using (deleteSettings.ConfigureAwait(false))
                {
                    deleteSettings.Transaction = transaction;
                    deleteSettings.CommandText = "DELETE FROM routing_settings WHERE list_id = $id;";
                    deleteSettings.Parameters.AddWithValue("$id", id);
                    await deleteSettings.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var deleteResolutions = connection.CreateCommand();
                await using (deleteResolutions.ConfigureAwait(false))
                {
                    // Drop cached resolutions that belonged to this list so they are not seeded after removal.
                    deleteResolutions.Transaction = transaction;
                    deleteResolutions.CommandText = "DELETE FROM domain_ips WHERE list_id = $id;";
                    deleteResolutions.Parameters.AddWithValue("$id", id);
                    await deleteResolutions.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
    public async Task<long?> GetActiveRoutingListGenerationAsync(string tunnel, CancellationToken ct = default)
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
                    SELECT rl.generation
                    FROM tunnel_geo tg
                    JOIN routing_lists rl ON rl.id = tg.proj_routing_list_id
                    WHERE tg.name = $name AND tg.projected = 1;
                    """;
                command.Parameters.AddWithValue("$name", tunnel);

                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<ActiveRoutingListMaterialization?> GetActiveRoutingListMaterializationAsync(string tunnel, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Read the list's current materialization, not the connect-time snapshot.
                command.CommandText =
                    """
                    SELECT rl.id, rl.generation, rl.routes_json, rl.domains_json
                    FROM tunnel_geo tg
                    JOIN routing_lists rl ON rl.id = tg.proj_routing_list_id
                    WHERE tg.name = $name AND tg.projected = 1;
                    """;
                command.Parameters.AddWithValue("$name", tunnel);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var listId = reader.GetInt64(0);
                    var generation = reader.GetInt64(1);
                    var routes = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [];
                    var domains = JsonSerializer.Deserialize<List<GeoDomain>>(reader.GetString(3)) ?? [];
                    return new ActiveRoutingListMaterialization(listId, generation, routes, domains);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<long>> ListAssignedRoutingListIdsAsync(CancellationToken ct = default)
    {
        var ids = new List<long>();
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT DISTINCT routing_list_id FROM profiles WHERE routing_list_id IS NOT NULL;";
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        ids.Add(reader.GetInt64(0));
                    }
                }
            }
        }

        return ids;
    }

    /// <inheritdoc/>
    public async Task<RoutingSettings?> GetRoutingSettingsAsync(long routingListId, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT exclusions, all_udp, mode, use_ipv6, use_global_proxy FROM routing_settings WHERE list_id = $id;";
                command.Parameters.AddWithValue("$id", routingListId);

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new RoutingSettings(routingListId, reader.GetString(0), reader.GetInt32(1) != 0, reader.GetString(2), reader.GetInt32(3) != 0, reader.GetInt32(4) != 0);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task SetRoutingSettingsAsync(RoutingSettings settings, CancellationToken ct = default)
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
                    INSERT INTO routing_settings (list_id, exclusions, all_udp, mode, use_ipv6, use_global_proxy, updated_at)
                    VALUES ($id, $excl, $udp, $mode, $v6, $globalProxy, $updated)
                    ON CONFLICT(list_id) DO UPDATE SET
                        exclusions       = excluded.exclusions,
                        all_udp          = excluded.all_udp,
                        mode             = excluded.mode,
                        use_ipv6         = excluded.use_ipv6,
                        use_global_proxy = excluded.use_global_proxy,
                        updated_at       = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$id", settings.ListId);
                command.Parameters.AddWithValue("$excl", settings.Exclusions);
                command.Parameters.AddWithValue("$udp", settings.AllUdp ? 1 : 0);
                command.Parameters.AddWithValue("$mode", settings.Mode);
                command.Parameters.AddWithValue("$v6", settings.UseIpv6 ? 1 : 0);
                command.Parameters.AddWithValue("$globalProxy", settings.UseGlobalProxy ? 1 : 0);
                command.Parameters.AddWithValue("$updated", Timestamp());
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public async Task RemoveRoutingSettingsAsync(long routingListId, CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM routing_settings WHERE list_id = $id;";
                command.Parameters.AddWithValue("$id", routingListId);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
                    FROM profiles
                    WHERE name = $name;
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
                    UPDATE profiles
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
        string directRoutesJson;
        string directDomainsJson;
        string blockRoutesJson;
        string blockDomainsJson;

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText =
                $"""
                SELECT id, name, routes_json, domains_json,
                       direct_routes_json, direct_domains_json, block_routes_json, block_domains_json
                FROM routing_lists WHERE {whereClause};
                """;
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
                directRoutesJson = reader.GetString(4);
                directDomainsJson = reader.GetString(5);
                blockRoutesJson = reader.GetString(6);
                blockDomainsJson = reader.GetString(7);
            }
        }

        var rules = await ReadRoutingListRulesAsync(connection, id, ct).ConfigureAwait(false);
        return BuildRoutingList(id, name, rules,
            routesJson, domainsJson, directRoutesJson, directDomainsJson, blockRoutesJson, blockDomainsJson);
    }

    // Deserializes the six materialized-bucket columns into a RoutingList; apps come from the Proxy-bucket rules.
    private static RoutingList BuildRoutingList(long id, string name, IReadOnlyList<GeoRule> rules,
        string routesJson, string domainsJson, string directRoutesJson, string directDomainsJson,
        string blockRoutesJson, string blockDomainsJson)
    {
        var routes = JsonSerializer.Deserialize<List<string>>(routesJson) ?? [];
        var domains = JsonSerializer.Deserialize<List<GeoDomain>>(domainsJson) ?? [];
        var directRoutes = JsonSerializer.Deserialize<List<string>>(directRoutesJson) ?? [];
        var directDomains = JsonSerializer.Deserialize<List<GeoDomain>>(directDomainsJson) ?? [];
        var blockRoutes = JsonSerializer.Deserialize<List<string>>(blockRoutesJson) ?? [];
        var blockDomains = JsonSerializer.Deserialize<List<GeoDomain>>(blockDomainsJson) ?? [];
        return new RoutingList(id, name, rules, routes, domains, ExtractApps(rules),
            directRoutes, directDomains, blockRoutes, blockDomains);
    }

    // Collect Proxy-bucket App-kind rule values as matcher tokens (per-app tunneling is a proxy concept).
    private static List<string> ExtractApps(IReadOnlyList<GeoRule> rules)
    {
        var apps = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.Kind == GeoRuleKind.App && rule.Role == RouteRole.Proxy)
            {
                apps.Add(rule.Value);
            }
        }

        return apps;
    }

    private static async Task<IReadOnlyList<GeoRule>> ReadRoutingListRulesAsync(SqliteConnection connection, long listId, CancellationToken ct)
    {
        var rules = new List<GeoRule>();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText =
                """
                SELECT kind, value, role
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
                    var role = Enum.TryParse<RouteRole>(reader.GetString(2), out var parsed) ? parsed : RouteRole.Proxy;
                    rules.Add(new GeoRule(kind, reader.GetString(1), role));
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

    private static ProfileState ReadProfileState(string name, SqliteDataReader reader, int offset = 0)
    {
        var updatedAt = DateTimeOffset.Parse(reader.GetString(offset + 1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new ProfileState(name, reader.GetString(offset), updatedAt);
    }
}
