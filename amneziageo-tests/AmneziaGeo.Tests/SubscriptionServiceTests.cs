using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Операции подписок, общие для всех агентов: заведение, список, обновление, снятие и расписание.
/// </summary>
public sealed class SubscriptionServiceTests : IAsyncLifetime
{
    private const string ClientKeySeed = "AmneziaGeo service client key ";

    // Ключ у подписки свой на каждый узел: по нему узел и опознаётся между чтениями.
    private static string ClientKey(string node)
    {
        var text = (node + " " + ClientKeySeed).PadRight(32)[..32];
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }
    private const string ServerKey = "QW1uZXppYUdlbyBzZXJ2aWNlIHNlcnZlciBrZXkhIQ==";
    private const string Url = "https://example.net:9080/sub/path/id";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-subsvc-{Guid.NewGuid():N}.db");
    private readonly Feed _feed = new();
    private SqliteStateStore _store = null!;
    private MemoryLibrary _library = null!;
    private SubscriptionService _service = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
        _library = new MemoryLibrary();
        _service = new SubscriptionService(
            new GeoHttp(new HttpClient(_feed), NullLogger<GeoHttp>.Instance),
            _store,
            _library);
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
    public async Task Add_BringsInTheConfigsAndNamesTheSubscriptionAfterTheHost()
    {
        _feed.Body = Body(Config("phone"), Config("laptop"));

        var outcome = await _service.AddAsync([Url], default);

        Assert.True(outcome.Ack.Ok);
        Assert.Equal(2, outcome.Added);
        var stored = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Equal("example.net", stored.Name);
        Assert.Equal(2, _library.Names.Count);
    }

    [Fact]
    public async Task Add_TakesTheNameItIsGiven()
    {
        _feed.Body = Body(Config("phone"));

        await _service.AddAsync([Url, "мой сервер"], default);

        Assert.Equal("мой сервер", (await _store.ListSubscriptionsAsync())[0].Name);
    }

    [Fact]
    public async Task Add_RefusesAnythingThatIsNotAnHttpAddress()
    {
        var outcome = await _service.AddAsync(["vpn://not-an-address"], default);

        Assert.False(outcome.Ack.Ok);
        Assert.Empty(await _store.ListSubscriptionsAsync());
    }

    [Fact]
    public async Task Add_RefusesATakenName()
    {
        _feed.Body = Body(Config("phone"));
        await _service.AddAsync([Url], default);

        var outcome = await _service.AddAsync([Url], default);

        Assert.False(outcome.Ack.Ok);
        Assert.Single(await _store.ListSubscriptionsAsync());
    }

    [Fact]
    public async Task Add_LeavesNothingBehindWhenTheAddressCannotBeRead()
    {
        _feed.Status = HttpStatusCode.Forbidden;

        var outcome = await _service.AddAsync([Url], default);

        Assert.False(outcome.Ack.Ok);
        Assert.Empty(await _store.ListSubscriptionsAsync());
        Assert.Empty(_library.Names);
    }

    [Fact]
    public async Task List_ReportsWhatTheSubscriptionCarries()
    {
        _feed.Body = Body(Config("phone"), Config("laptop"));
        await _service.AddAsync([Url], default);
        _feed.Body = Body(Config("phone"));
        await _service.RefreshAsync([], default);

        var ack = await _service.ListAsync(default);

        Assert.True(ack.Ok);
        var entry = Assert.Single(JsonSerializer.Deserialize<List<SubscriptionEntry>>(ack.Message, IpcJson.Options)!);
        Assert.Equal("example.net", entry.Name);
        Assert.Equal(1, entry.Configs);
        Assert.Equal(0, entry.Gone);
        Assert.Equal(12, entry.IntervalHours);
        Assert.NotEqual(0, entry.CheckedAt);
    }

    [Fact]
    public async Task Refresh_ByNameOfNothing_Refuses()
    {
        var outcome = await _service.RefreshAsync(["absent"], default);

        Assert.False(outcome.Ack.Ok);
    }

    [Fact]
    public async Task Refresh_NamesTheConfigsItRewrote()
    {
        _feed.Body = Body(Config("phone"));
        await _service.AddAsync([Url], default);
        _feed.Body = Body(Config("phone", "example.net:51999"));

        var outcome = await _service.RefreshAsync([], default);

        Assert.True(outcome.Ack.Ok);
        Assert.Equal(1, outcome.Updated);
        Assert.Equal(["phone"], outcome.Rewritten);
    }

    [Fact]
    public async Task Remove_KeepsTheConfigsUnlessAskedOtherwise()
    {
        _feed.Body = Body(Config("phone"));
        await _service.AddAsync([Url], default);

        var ack = await _service.RemoveAsync(["example.net"], null, default);

        Assert.True(ack.Ok);
        Assert.Empty(await _store.ListSubscriptionsAsync());
        Assert.Single(_library.Names);
    }

    [Fact]
    public async Task Remove_WithConfigs_TakesThemToo()
    {
        _feed.Body = Body(Config("phone"));
        await _service.AddAsync([Url], default);

        var ack = await _service.RemoveAsync(["example.net", "configs"], null, default);

        Assert.True(ack.Ok);
        Assert.Empty(_library.Names);
    }

    [Fact]
    public async Task Remove_WithConfigs_RefusesWhileOneOfThemIsRunning()
    {
        _feed.Body = Body(Config("phone"));
        await _service.AddAsync([Url], default);

        var ack = await _service.RemoveAsync(["example.net", "configs"], "phone", default);

        Assert.False(ack.Ok);
        Assert.Single(_library.Names);
        Assert.Single(await _store.ListSubscriptionsAsync());
    }

    [Fact]
    public async Task ConfigUrl_AnswersForAConfigOfTheSubscriptionAndIsEmptyForAnyOther()
    {
        _feed.Body = Body(Config("phone"));
        await _service.AddAsync([Url], default);

        Assert.Equal(Url, (await _service.ConfigUrlAsync(["phone"], default)).Message);
        Assert.Equal(string.Empty, (await _service.ConfigUrlAsync(["elsewhere"], default)).Message);
    }

    [Fact]
    public async Task Due_HoldsUntilTheServersOwnIntervalRunsOut()
    {
        _feed.Body = Body(Config("phone"));
        await _service.AddAsync([Url], default);
        var now = DateTimeOffset.UtcNow;

        Assert.Empty(await _service.DueAsync(1, now.AddHours(11), default));
        Assert.Single(await _service.DueAsync(1, now.AddHours(13), default));
    }

    [Fact]
    public void Due_TakesTheSettingWhereTheServerNamedNoInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var subscription = new Subscription("s", Url, CheckedAt: now.AddHours(-5));

        Assert.False(SubscriptionService.Due(subscription, 12, now));
        Assert.True(SubscriptionService.Due(subscription, 4, now));
    }

    [Fact]
    public void Due_IsTrueForASubscriptionNeverRead()
    {
        Assert.True(SubscriptionService.Due(new Subscription("s", Url), 12, DateTimeOffset.UtcNow));
    }

    private static string Config(string remark, string endpoint = "example.net:51821")
    {
        return string.Join(
            '\n',
            [
                "[Interface]",
                $"PrivateKey = {ClientKey(remark)}",
                "Address = 10.0.1.2/32",
                "Jc = 6",
                "Jmin = 52",
                "Jmax = 241",
                string.Empty,
                $"# {remark}",
                "[Peer]",
                $"PublicKey = {ServerKey}",
                "AllowedIPs = 0.0.0.0/0, ::/0",
                $"Endpoint = {endpoint}",
                "PersistentKeepalive = 25",
            ]);
    }

    private static string Body(params string[] configs)
    {
        var links = configs.Select(config =>
            "vpn://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(config)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', links)));
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

    // Панель x-ui: тело в base64 и заголовки о профиле.
    private sealed class Feed : HttpMessageHandler
    {
        public string Body { get; set; } = string.Empty;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "text/plain"),
            };
            response.Headers.TryAddWithoutValidation("Profile-Update-Interval", "12");
            response.Headers.TryAddWithoutValidation("Subscription-Userinfo", "upload=1; download=2; total=0; expire=0");
            return Task.FromResult(response);
        }
    }

    // Библиотека в памяти вместо конфигураций агента.
    private sealed class MemoryLibrary : ISubscriptionLibrary
    {
        private readonly Dictionary<string, string> _configs = [];

        public IReadOnlyList<string> Names => [.. _configs.Keys];

        public Task<IReadOnlyCollection<string>> NamesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyCollection<string>>(_configs.Keys.ToList());
        }

        public Task<string?> TextAsync(string name, CancellationToken ct)
        {
            return Task.FromResult(_configs.TryGetValue(name, out var text) ? text : null);
        }

        public Task AddAsync(string name, string confText, CancellationToken ct)
        {
            _configs[name] = confText;
            return Task.CompletedTask;
        }

        public Task EditAsync(string name, string confText, CancellationToken ct)
        {
            _configs[name] = confText;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string name, CancellationToken ct)
        {
            _configs.Remove(name);
            return Task.CompletedTask;
        }
    }
}
