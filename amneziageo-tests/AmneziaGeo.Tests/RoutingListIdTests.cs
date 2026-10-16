using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Routing lists are told apart by their lasting id: the name repeats, and a database where the name was unique
/// moves over with its lists, rules, settings and ids.
/// </summary>
public sealed class RoutingListIdTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-listid-{Guid.NewGuid():N}.db");

    /// <inheritdoc />
    public void Dispose()
    {
        ClearPool();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + ".pre-list-ids.bak" })
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task TwoListsUnderOneName_LiveSideBySide()
    {
        var store = await OpenAsync();

        var first = await store.SaveRoutingListAsync(List("мой", "10.1.0.0/16"));
        var second = await store.SaveRoutingListAsync(List("мой", "10.2.0.0/16"));

        Assert.NotEqual(first, second);
        var lists = await store.ListRoutingListsAsync();
        Assert.Equal(["мой", "мой"], lists.Select(list => list.Name));
        Assert.Equal(["10.1.0.0/16"], lists.Single(list => list.Id == first).Routes);
        Assert.Equal(["10.2.0.0/16"], lists.Single(list => list.Id == second).Routes);
    }

    [Fact]
    public async Task EveryNewList_GetsALastingIdOfItsOwn()
    {
        var store = await OpenAsync();

        var first = await store.GetRoutingListAsync(await store.SaveRoutingListAsync(List("a", "10.1.0.0/16")));
        var second = await store.GetRoutingListAsync(await store.SaveRoutingListAsync(List("a", "10.2.0.0/16")));

        Assert.Equal(32, first!.Uid.Length);
        Assert.Equal(32, second!.Uid.Length);
        Assert.NotEqual(first.Uid, second.Uid);
        Assert.Equal(second.Id, (await store.GetRoutingListByUidAsync(second.Uid))!.Id);
    }

    [Fact]
    public async Task AGivenId_IsKept_AndATakenOneGivesWayToAFreshOne()
    {
        var store = await OpenAsync();

        var given = await store.GetRoutingListAsync(await store.SaveRoutingListAsync(List("a", "10.1.0.0/16") with { Uid = "server-7" }));
        var again = await store.GetRoutingListAsync(await store.SaveRoutingListAsync(List("a", "10.2.0.0/16") with { Uid = "server-7" }));

        Assert.Equal("server-7", given!.Uid);
        Assert.NotEqual("server-7", again!.Uid);
        Assert.Equal(32, again.Uid.Length);
    }

    [Fact]
    public async Task ARewrite_KeepsTheId()
    {
        var store = await OpenAsync();
        var id = await store.SaveRoutingListAsync(List("a", "10.1.0.0/16"));
        var uid = (await store.GetRoutingListAsync(id))!.Uid;

        await store.SaveRoutingListAsync(List("b", "10.2.0.0/16") with { Id = id });

        var rewritten = await store.GetRoutingListAsync(id);
        Assert.Equal("b", rewritten!.Name);
        Assert.Equal(uid, rewritten.Uid);
    }

    [Fact]
    public async Task TheOrder_GoesByIdWhenNamesRepeat()
    {
        var store = await OpenAsync();
        var first = await store.SaveRoutingListAsync(List("a", "10.1.0.0/16"));
        var second = await store.SaveRoutingListAsync(List("a", "10.2.0.0/16"));

        await store.SetRoutingListOrderAsync([second, first]);

        Assert.Equal([second, first], (await store.ListRoutingListSummariesAsync()).Select(summary => summary.Id));
    }

    [Fact]
    public async Task ADatabaseWithUniqueNames_MovesOverWithItsListsRulesSettingsAndIds()
    {
        Seed();

        var store = await OpenAsync();

        var lists = await store.ListRoutingListsAsync();
        Assert.Equal([3L, 5L], lists.Select(list => list.Id));
        Assert.Equal(["a", "b"], lists.Select(list => list.Name));
        Assert.Equal("preset-5", lists.Single(list => list.Id == 5).Uid);
        Assert.Equal(32, lists.Single(list => list.Id == 3).Uid.Length);
        Assert.Equal("a.example", Assert.Single(lists.Single(list => list.Id == 3).Rules).Value);
        Assert.Equal("b.example", Assert.Single(lists.Single(list => list.Id == 5).Rules).Value);
        Assert.True((await store.GetRoutingSettingsAsync(5))!.AllUdp);
        Assert.Equal("preset-5", (await store.GetRoutingListOriginAsync(5))!.PresetId);

        // The name repeats now, and a new list takes no id a removed one held.
        var added = await store.SaveRoutingListAsync(List("a", "10.3.0.0/16"));
        Assert.Equal(10, added);
        Assert.Equal(["a", "b", "a"], (await store.ListRoutingListsAsync()).Select(list => list.Name));
        Assert.True(File.Exists(_dbPath + ".pre-list-ids.bak"));
    }

    [Fact]
    public async Task ASecondStart_LeavesTheMovedDatabaseAlone()
    {
        Seed();
        var uids = (await (await OpenAsync()).ListRoutingListsAsync()).Select(list => list.Uid).ToList();
        ClearPool();
        TryDelete(_dbPath + ".pre-list-ids.bak");

        var again = await OpenAsync();

        Assert.Equal(uids, (await again.ListRoutingListsAsync()).Select(list => list.Uid));
        Assert.False(File.Exists(_dbPath + ".pre-list-ids.bak"));
    }

    private static RoutingList List(string name, string route)
    {
        return new RoutingList(0, name, [], [route], [], [], [], [], [], [], []);
    }

    // A database as an earlier build left it: the name of a routing list is unique, the id counter has run past
    // lists that are gone, list 5 came from a server.
    private void Seed()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 1;

            CREATE TABLE routing_lists (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                name                TEXT NOT NULL UNIQUE,
                routes_json         TEXT NOT NULL,
                domains_json        TEXT NOT NULL,
                direct_routes_json  TEXT NOT NULL DEFAULT '[]',
                direct_domains_json TEXT NOT NULL DEFAULT '[]',
                block_routes_json   TEXT NOT NULL DEFAULT '[]',
                block_domains_json  TEXT NOT NULL DEFAULT '[]',
                exclude_routes_json  TEXT NOT NULL DEFAULT '[]',
                exclude_domains_json TEXT NOT NULL DEFAULT '[]',
                updated_at          TEXT NOT NULL
            );

            CREATE TABLE routing_list_rules (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                list_id    INTEGER NOT NULL REFERENCES routing_lists(id) ON DELETE CASCADE,
                kind       TEXT NOT NULL,
                value      TEXT NOT NULL,
                role       TEXT NOT NULL DEFAULT 'Proxy',
                position   INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE (list_id, position)
            );

            CREATE TABLE routing_settings (
                list_id          INTEGER PRIMARY KEY REFERENCES routing_lists(id) ON DELETE CASCADE,
                exclusions       TEXT NOT NULL DEFAULT '',
                all_udp          INTEGER NOT NULL DEFAULT 0,
                mode             TEXT NOT NULL DEFAULT 'split',
                use_ipv6         INTEGER NOT NULL DEFAULT 0,
                use_global_proxy INTEGER NOT NULL DEFAULT 0,
                updated_at       TEXT NOT NULL
            );

            CREATE TABLE routing_list_origins (
                list_id    INTEGER PRIMARY KEY,
                preset_id  TEXT NOT NULL UNIQUE,
                source     TEXT NOT NULL DEFAULT '',
                updated    TEXT NOT NULL DEFAULT '',
                pending    TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );

            INSERT INTO routing_lists (id, name, routes_json, domains_json, updated_at)
                VALUES (3, 'a', '[]', '[]', '2026-10-01'), (5, 'b', '[]', '[]', '2026-10-01');
            UPDATE sqlite_sequence SET seq = 9 WHERE name = 'routing_lists';
            INSERT INTO routing_list_rules (list_id, kind, value, role, position, updated_at)
                VALUES (3, 'Domain', 'a.example', 'Proxy', 0, '2026-10-01'), (5, 'Domain', 'b.example', 'Proxy', 0, '2026-10-01');
            INSERT INTO routing_settings (list_id, all_udp, updated_at) VALUES (5, 1, '2026-10-01');
            INSERT INTO routing_list_origins (list_id, preset_id, source, updated_at) VALUES (5, 'preset-5', 'srv', '2026-10-01');
            """;
        command.ExecuteNonQuery();
    }

    private async Task<SqliteStateStore> OpenAsync()
    {
        var store = new SqliteStateStore(_dbPath);
        await store.InitializeAsync();
        return store;
    }

    // Drops the pooled connections to this test's database alone.
    private void ClearPool()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        SqliteConnection.ClearPool(connection);
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
