using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Обновление базы правил в два шага: загрузка без записи и запись загруженного.
/// </summary>
public sealed class GeoFileUpdaterTests : IAsyncLifetime
{
    private const string Tag = "\"v1\"";

    private static readonly GeoSource _source = new("geoip-stand", "geoip", "http://stand.test/geoip.dat", 1);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-geofile-{Guid.NewGuid():N}.db");
    private readonly Feed _feed = new();
    private readonly Files _files = new();
    private SqliteStateStore _store = null!;
    private GeoFileUpdater _updater = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
        await _store.SaveGeoSourceAsync(_source);
        _updater = new GeoFileUpdater(_store, new GeoHttp(new HttpClient(_feed), NullLogger<GeoHttp>.Instance), _files);
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.ClearPool();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            Remove(path);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AFetch_WritesNothing()
    {
        _feed.Body = GeoIp("TT");
        _feed.Tag = Tag;

        var fetched = await _updater.FetchAsync(_source);

        Assert.Equal(_feed.Body, fetched.Data);
        Assert.Equal(1, fetched.Count);
        Assert.Null(fetched.Existing);
        Assert.Empty(_files.Written);
        Assert.Null(await _store.GetGeoFileAsync(_source.Name));
    }

    [Fact]
    public async Task AStore_WritesTheFileAndItsRecord()
    {
        _feed.Body = GeoIp("TT");
        _feed.Tag = Tag;
        var fetched = await _updater.FetchAsync(_source);

        var metadata = await _updater.StoreAsync(fetched);

        Assert.Equal(_feed.Body, _files.Written[_source.Name]);
        Assert.Equal(1, metadata.CategoryCount);
        Assert.Equal(Tag, metadata.ETag);
        var kept = await _store.GetGeoFileAsync(_source.Name);
        Assert.Equal(metadata.Sha256, kept?.Sha256);
    }

    [Fact]
    public async Task AFileThatDidNotChange_KeepsWhatWasRecorded()
    {
        _feed.Body = GeoIp("TT");
        _feed.Tag = Tag;
        var first = await _updater.UpdateAsync(_source);

        var fetched = await _updater.FetchAsync(_source);
        var second = await _updater.StoreAsync(fetched);

        Assert.Null(fetched.Data);
        Assert.Equal(first.Sha256, fetched.Existing?.Sha256);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(1, _files.Writes);
    }

    [Fact]
    public async Task ASourceThatMoved_IsFetchedWhateverItsOldAddressSaid()
    {
        _feed.Body = GeoIp("TT");
        _feed.Tag = Tag;
        await _updater.UpdateAsync(_source);
        _feed.Body = GeoIp("UU");

        var fetched = await _updater.FetchAsync(_source with { Url = "http://mirror.test/geoip.dat" });

        Assert.Equal(_feed.Body, fetched.Data);
    }

    [Fact]
    public async Task AFileWithoutACategory_FailsAtTheFetch()
    {
        _feed.Body = [];

        await Assert.ThrowsAsync<InvalidDataException>(() => _updater.FetchAsync(_source));

        Assert.Empty(_files.Written);
        Assert.Null(await _store.GetGeoFileAsync(_source.Name));
    }

    // Файл geoip с одной страной и одной сетью.
    private static byte[] GeoIp(string code)
    {
        byte[] network = [0x0A, 0x04, 198, 18, 0, 0, 0x10, 24];
        byte[] country = [0x0A, (byte)code.Length, .. Encoding.ASCII.GetBytes(code), 0x12, (byte)network.Length, .. network];
        return [0x0A, (byte)country.Length, .. country];
    }

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    // Отдаёт файл базы с меткой и отвечает 304 на метку, которую клиент уже держит.
    private sealed class Feed : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];

        public string? Tag { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Tag is not null && request.Headers.IfNoneMatch.Any(held => held.Tag == Tag))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) };
            if (Tag is not null)
            {
                response.Headers.ETag = new EntityTagHeaderValue(Tag);
            }

            return Task.FromResult(response);
        }
    }

    // Файлы баз в памяти.
    private sealed class Files : IGeoFileStore
    {
        public Dictionary<string, byte[]> Written { get; } = new(StringComparer.Ordinal);

        public int Writes { get; private set; }

        public byte[]? Read(string name) => Written.GetValueOrDefault(name);

        public Stream? OpenRead(string name) => Written.TryGetValue(name, out var data) ? new MemoryStream(data) : null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default)
        {
            Written[name] = data;
            Writes++;
            return Task.CompletedTask;
        }
    }
}
