using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Подписка и принадлежность конфигураций к ней на настоящей базе.
/// </summary>
public sealed class SubscriptionStoreTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-sub-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.ClearPool();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Subscription_ComesBackWholeWithWhatThePanelReported()
    {
        var expires = DateTimeOffset.FromUnixTimeSeconds(1790000000);
        var checkedAt = DateTimeOffset.FromUnixTimeSeconds(1787830609);

        await _store.SaveSubscriptionAsync(new Subscription(
            "myvpn",
            "https://example.net:9080/sub/x/y",
            "Мой профиль",
            12,
            1024,
            2048,
            10737418240,
            expires,
            checkedAt,
            "нет связи"));

        var stored = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Equal("myvpn", stored.Name);
        Assert.Equal("https://example.net:9080/sub/x/y", stored.Url);
        Assert.Equal("Мой профиль", stored.Title);
        Assert.Equal(12, stored.IntervalHours);
        Assert.Equal(1024, stored.Upload);
        Assert.Equal(2048, stored.Download);
        Assert.Equal(10737418240, stored.Total);
        Assert.Equal(expires, stored.Expires);
        Assert.Equal(checkedAt, stored.CheckedAt);
        Assert.Equal("нет связи", stored.LastError);
    }

    [Fact]
    public async Task Subscription_WithoutMoments_ComesBackWithoutThem()
    {
        await _store.SaveSubscriptionAsync(new Subscription("myvpn", "https://host/sub/x"));

        var stored = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Null(stored.Expires);
        Assert.Null(stored.CheckedAt);
    }

    [Fact]
    public async Task Subscription_SavedTwice_IsOneRow()
    {
        await _store.SaveSubscriptionAsync(new Subscription("myvpn", "https://host/sub/x"));
        await _store.SaveSubscriptionAsync(new Subscription("myvpn", "https://host/sub/y", IntervalHours: 6));

        var stored = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Equal("https://host/sub/y", stored.Url);
        Assert.Equal(6, stored.IntervalHours);
    }

    [Fact]
    public async Task Members_AreListedByTheirSubscription()
    {
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "AmneziaWG 3.1 -phone", "phone"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "AmneziaWG 2 -laptop", "laptop"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("other", "node", "other-node"));

        Assert.Equal(2, (await _store.ListSubscriptionMembersAsync("myvpn")).Count);
        Assert.Equal(3, (await _store.ListSubscriptionMembersAsync()).Count);
    }

    [Fact]
    public async Task Member_SavedTwice_KeepsTheLastNameAndFlag()
    {
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone-2", Present: false));

        var member = Assert.Single(await _store.ListSubscriptionMembersAsync("myvpn"));
        Assert.Equal("phone-2", member.ConfigName);
        Assert.False(member.Present);
    }

    [Fact]
    public async Task RemovingASubscription_TakesItsMembersAndLeavesTheRest()
    {
        await _store.SaveSubscriptionAsync(new Subscription("myvpn", "https://host/sub/x"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("other", "node", "other-node"));

        await _store.RemoveSubscriptionAsync("myvpn");

        Assert.Empty(await _store.ListSubscriptionsAsync());
        var member = Assert.Single(await _store.ListSubscriptionMembersAsync());
        Assert.Equal("other", member.Subscription);
    }

    [Fact]
    public async Task RemovingAMember_LeavesTheOthers()
    {
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "second", "laptop"));

        await _store.RemoveSubscriptionMemberAsync("myvpn", "node");

        var member = Assert.Single(await _store.ListSubscriptionMembersAsync("myvpn"));
        Assert.Equal("second", member.Remark);
    }

    [Fact]
    public async Task RenamedConfig_IsFollowedByEveryMembership()
    {
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("other", "node", "phone"));

        await _store.RenameSubscriptionMemberAsync("phone", "phone-renamed");

        var members = await _store.ListSubscriptionMembersAsync();
        Assert.All(members, member => Assert.Equal("phone-renamed", member.ConfigName));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
