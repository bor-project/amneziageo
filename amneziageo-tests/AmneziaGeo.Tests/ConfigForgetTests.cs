using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Что остаётся от подписки, когда её конфигурацию удаляют руками.
/// </summary>
public sealed class ConfigForgetTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-forget-{Guid.NewGuid():N}.db");
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
    public async Task LastConfigTakesTheSubscriptionWithIt()
    {
        await _store.SaveSubscriptionAsync(new Subscription("myvpn", "https://example.net:9080/sub/x/y"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone"));

        await ConfigForget.CarryAsync(_store, "phone");

        Assert.Empty(await _store.ListSubscriptionsAsync());
        Assert.Empty(await _store.ListSubscriptionMembersAsync(null));
    }

    [Fact]
    public async Task RemainingConfigKeepsTheSubscription()
    {
        await _store.SaveSubscriptionAsync(new Subscription("myvpn", "https://example.net:9080/sub/x/y"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "second", "laptop"));

        await ConfigForget.CarryAsync(_store, "phone");

        Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Equal("laptop", Assert.Single(await _store.ListSubscriptionMembersAsync(null)).ConfigName);
    }

    [Fact]
    public async Task OtherSubscriptionsStayWhereTheyAre()
    {
        await _store.SaveSubscriptionAsync(new Subscription("myvpn", "https://example.net:9080/sub/x/y"));
        await _store.SaveSubscriptionAsync(new Subscription("other", "https://example.org:9080/sub/a/b"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("other", "node", "laptop"));

        await ConfigForget.CarryAsync(_store, "phone");

        Assert.Equal("other", Assert.Single(await _store.ListSubscriptionsAsync()).Name);
        Assert.Equal("laptop", Assert.Single(await _store.ListSubscriptionMembersAsync(null)).ConfigName);
    }

    [Fact]
    public async Task ConfigOfNoSubscriptionChangesNothing()
    {
        await _store.SaveSubscriptionAsync(new Subscription("myvpn", "https://example.net:9080/sub/x/y"));
        await _store.SaveSubscriptionMemberAsync(new SubscriptionMember("myvpn", "node", "phone"));

        await ConfigForget.CarryAsync(_store, "own");

        Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Single(await _store.ListSubscriptionMembersAsync(null));
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
