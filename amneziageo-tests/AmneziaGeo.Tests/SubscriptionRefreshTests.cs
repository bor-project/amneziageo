using System.Net;
using System.Net.Http;
using System.Text;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Обновление подписки целиком: чтение по HTTP, сведение с библиотекой и запись в базу.
/// </summary>
public sealed class SubscriptionRefreshTests : IAsyncLifetime
{
    private const string ClientKeySeed = "AmneziaGeo refresh client key ";

    // Ключ у подписки свой на каждый узел: по нему узел и опознаётся между чтениями.
    private static string ClientKey(string node)
    {
        var text = (node + " " + ClientKeySeed).PadRight(32)[..32];
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }
    private const string ServerKey = "QW1uZXppYUdlbyByZWZyZXNoIHNlcnZlciBrZXkhIQ==";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-refresh-{Guid.NewGuid():N}.db");
    private readonly Feed _feed = new();
    private SqliteStateStore _store = null!;
    private MemoryLibrary _library = null!;
    private SubscriptionRefresher _refresher = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
        _library = new MemoryLibrary();
        _refresher = new SubscriptionRefresher(new GeoHttp(new HttpClient(_feed), NullLogger<GeoHttp>.Instance), _store, _library);
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

    private static string Config(string remark, string endpoint = "example.net:51821", string address = "10.0.1.2/32")
    {
        return string.Join(
            '\n',
            [
                "[Interface]",
                $"PrivateKey = {ClientKey(remark)}",
                $"Address = {address}",
                "Jc = 6",
                "Jmin = 52",
                "Jmax = 241",
                "S1 = 63",
                "S2 = 149",
                "H1 = 194488238-453280017",
                "H2 = 945380663-959625713",
                "H3 = 1220926369-1460941108",
                "H4 = 2008138652-2111657743",
                string.Empty,
                $"# {remark}",
                "[Peer]",
                $"PublicKey = {ServerKey}",
                "AllowedIPs = 0.0.0.0/0, ::/0",
                $"Endpoint = {endpoint}",
                "PersistentKeepalive = 25",
            ]);
    }

    private static string Link(string config)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(config));
        return "vpn://" + raw.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Body(params string[] configs)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', configs.Select(Link))));
    }

    private static Subscription Fresh()
    {
        return new Subscription("myvpn", "https://example.net:9080/sub/path/id");
    }

    [Fact]
    public async Task FirstRead_BringsInTheConfigsAndWhatThePanelReports()
    {
        _feed.Body = Body(Config("AmneziaWG 3.1 -phone"), Config("AmneziaWG 2 -laptop"));

        var result = await _refresher.RefreshAsync(Fresh(), default);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Added);
        Assert.Equal(["AmneziaWG-3.1-phone", "AmneziaWG-2-laptop"], _library.Names);

        var stored = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Equal(12, stored.IntervalHours);
        Assert.Equal("Мой профиль", stored.Title);
        Assert.Equal(3612439, stored.Upload);
        Assert.Equal(94739918, stored.Download);
        Assert.NotNull(stored.CheckedAt);
        Assert.Equal(string.Empty, stored.LastError);
        Assert.Equal(2, (await _store.ListSubscriptionMembersAsync("myvpn")).Count);
    }

    [Fact]
    public async Task SecondReadOfTheSameFeed_RewritesNothing()
    {
        _feed.Body = Body(Config("AmneziaWG 3.1 -phone"));
        await _refresher.RefreshAsync(Fresh(), default);

        var result = await _refresher.RefreshAsync(await Stored(), default);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Single(_library.Names);
    }

    [Fact]
    public async Task ChangedNode_IsRewrittenUnderItsOwnName()
    {
        _feed.Body = Body(Config("AmneziaWG 3.1 -phone"));
        await _refresher.RefreshAsync(Fresh(), default);
        _feed.Body = Body(Config("AmneziaWG 3.1 -phone", "example.net:51999"));

        var result = await _refresher.RefreshAsync(await Stored(), default);

        Assert.Equal(1, result.Updated);
        Assert.Equal(["AmneziaWG-3.1-phone"], result.Rewritten);
        Assert.Single(_library.Names);
        Assert.Contains("51999", _library.Text("AmneziaWG-3.1-phone"));
    }

    [Fact]
    public async Task NodeGoneFromTheFeed_TakesItsConfigWithIt()
    {
        _feed.Body = Body(Config("AmneziaWG 3.1 -phone"), Config("AmneziaWG 2 -laptop"));
        await _refresher.RefreshAsync(Fresh(), default);
        _feed.Body = Body(Config("AmneziaWG 3.1 -phone"));

        var result = await _refresher.RefreshAsync(await Stored(), default);

        Assert.Equal(1, result.Gone);
        Assert.Equal(["AmneziaWG-3.1-phone"], _library.Names);
        var members = await _store.ListSubscriptionMembersAsync("myvpn");
        Assert.Single(members);
        Assert.Equal("AmneziaWG-3.1-phone", members[0].ConfigName);
    }

    [Fact]
    public async Task ForeignProtocolsInTheFeed_AreSkipped()
    {
        _feed.Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            [
                Link(Config("AmneziaWG 3.1 -phone")),
                "vless://11111111-2222-3333-4444-555555555555@example.net:2096?type=xhttp#vless",
                "trojan://secret@example.net:443#trojan",
            ])));

        var result = await _refresher.RefreshAsync(Fresh(), default);

        Assert.Equal(1, result.Added);
    }

    [Fact]
    public async Task PanelThatRefuses_LeavesTheReasonOnTheSubscription()
    {
        _feed.Status = HttpStatusCode.Forbidden;

        var result = await _refresher.RefreshAsync(Fresh(), default);

        Assert.False(result.Ok);
        Assert.Empty(_library.Names);
        var stored = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.NotEqual(string.Empty, stored.LastError);
        Assert.NotNull(stored.CheckedAt);
    }

    [Fact]
    public async Task PanelIsAskedForTheListRatherThanThePage()
    {
        // На Accept: text/html панель отдаёт свою HTML-страницу вместо ссылок.
        _feed.Body = Body(Config("AmneziaWG 3.1 -phone"));

        await _refresher.RefreshAsync(Fresh(), default);

        Assert.NotNull(_feed.Accept);
        Assert.DoesNotContain("text/html", _feed.Accept!, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Subscription> Stored()
    {
        return (await _store.ListSubscriptionsAsync())[0];
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

        public string? Accept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Accept = request.Headers.TryGetValues("Accept", out var values) ? string.Join(',', values) : null;

            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "text/plain"),
            };
            response.Headers.TryAddWithoutValidation("Profile-Update-Interval", "12");
            response.Headers.TryAddWithoutValidation("Subscription-Userinfo", "upload=3612439; download=94739918; total=0; expire=0");
            response.Headers.TryAddWithoutValidation("Profile-Title", "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Мой профиль")));
            return Task.FromResult(response);
        }
    }

    // Библиотека в памяти вместо конфигураций агента.
    private sealed class MemoryLibrary : ISubscriptionLibrary
    {
        private readonly Dictionary<string, string> _configs = [];

        public IReadOnlyList<string> Names => [.. _configs.Keys];

        public string Text(string name) => _configs[name];

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
