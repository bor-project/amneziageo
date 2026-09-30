using System.Net;
using System.Net.Http.Headers;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A download of a release file that broke off keeps what it got, and the next attempt asks only for the rest; a
/// server that cannot give the rest, or a leftover that is not the start of this file, starts it over.
/// </summary>
public sealed class UpdateDownloadTests : IDisposable
{
    private static readonly Uri Address = new("https://example.test/releases/download/v9.9.9.9/AmneziaGeo-9.9.9.9-win-x64-fdd.exe");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ag-download-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _body = Bytes(300_000);

    /// <summary>
    /// ctor
    /// </summary>
    public UpdateDownloadTests()
    {
        Directory.CreateDirectory(_root);
    }

    private string Target => Path.Combine(_root, "setup.exe");

    private string Partial => Target + ".part";

    [Fact]
    public async Task ADownload_LandsWhole()
    {
        var feed = new Feed(_body);
        var progress = new List<(long Done, long Total)>();

        await UpdateDownload.DownloadAsync(new HttpClient(feed), Address, Target, (done, total) => progress.Add((done, total)), CancellationToken.None);

        Assert.Equal(_body, await File.ReadAllBytesAsync(Target));
        Assert.False(File.Exists(Partial));
        Assert.Equal(new[] { "whole" }, feed.Asked);
        Assert.Equal((_body.Length, _body.Length), progress[^1]);
    }

    [Fact]
    public async Task ADropMidway_KeepsWhatCameIn()
    {
        var feed = new Feed(_body) { DropAfter = 100_000 };

        await Assert.ThrowsAsync<IOException>(() => UpdateDownload.DownloadAsync(new HttpClient(feed), Address, Target, null, CancellationToken.None));

        Assert.False(File.Exists(Target));
        Assert.Equal(_body[..100_000], await File.ReadAllBytesAsync(Partial));
    }

    [Fact]
    public async Task TheNextAttempt_AsksOnlyForTheRest()
    {
        await File.WriteAllBytesAsync(Partial, _body[..100_000]);
        var feed = new Feed(_body);
        var progress = new List<(long Done, long Total)>();

        await UpdateDownload.DownloadAsync(new HttpClient(feed), Address, Target, (done, total) => progress.Add((done, total)), CancellationToken.None);

        Assert.Equal(_body, await File.ReadAllBytesAsync(Target));
        Assert.Equal(new[] { "100000-" }, feed.Asked);
        Assert.Equal((_body.Length, _body.Length), progress[^1]);
        Assert.True(progress[0].Done > 100_000);
    }

    [Fact]
    public async Task AServerThatIgnoresTheRange_StartsOver()
    {
        await File.WriteAllBytesAsync(Partial, Bytes(100_000, 7));
        var feed = new Feed(_body) { Ranges = false };

        await UpdateDownload.DownloadAsync(new HttpClient(feed), Address, Target, null, CancellationToken.None);

        Assert.Equal(_body, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task ALeftoverLongerThanTheFile_StartsOver()
    {
        await File.WriteAllBytesAsync(Partial, Bytes(400_000, 7));
        var feed = new Feed(_body);

        await UpdateDownload.DownloadAsync(new HttpClient(feed), Address, Target, null, CancellationToken.None);

        Assert.Equal(_body, await File.ReadAllBytesAsync(Target));
        Assert.Equal(new[] { "400000-", "whole" }, feed.Asked);
    }

    [Fact]
    public async Task APartFromAnotherPlace_StartsOver()
    {
        await File.WriteAllBytesAsync(Partial, _body[..100_000]);
        var feed = new Feed(_body) { AnswerFrom = 0 };

        await UpdateDownload.DownloadAsync(new HttpClient(feed), Address, Target, null, CancellationToken.None);

        Assert.Equal(_body, await File.ReadAllBytesAsync(Target));
        Assert.Equal(new[] { "100000-", "whole" }, feed.Asked);
    }

    [Fact]
    public async Task AShortAnswer_IsNoDownload()
    {
        var feed = new Feed(_body) { Short = true };

        await Assert.ThrowsAsync<IOException>(() => UpdateDownload.DownloadAsync(new HttpClient(feed), Address, Target, null, CancellationToken.None));

        Assert.False(File.Exists(Target));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private static byte[] Bytes(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    // A release server that honours ranges or not, and can break the connection partway.
    private sealed class Feed(byte[] body) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        public bool Ranges { get; init; } = true;

        public long DropAfter { get; init; } = long.MaxValue;

        public long? AnswerFrom { get; init; }

        public bool Short { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.First();
            Asked.Add(range is null ? "whole" : $"{range.From}-{range.To}");
            if (range is null || !Ranges)
            {
                return Task.FromResult(Answer(HttpStatusCode.OK, 0));
            }

            if (range.From >= body.Length)
            {
                var refused = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                refused.Content.Headers.ContentRange = new ContentRangeHeaderValue(body.Length);
                return Task.FromResult(refused);
            }

            var from = AnswerFrom ?? range.From!.Value;
            var response = Answer(HttpStatusCode.PartialContent, from);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            return Task.FromResult(response);
        }

        private HttpResponseMessage Answer(HttpStatusCode code, long from)
        {
            var slice = body[(int)from..];
            var content = new StreamContent(new Breaking(Short ? slice[..(slice.Length / 2)] : slice, DropAfter - from));
            content.Headers.ContentLength = slice.Length;
            return new HttpResponseMessage(code) { Content = content };
        }
    }

    // A body that stops with a broken connection after the bytes given.
    private sealed class Breaking(byte[] data, long until) : Stream
    {
        private long _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _at;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= until && _at < data.Length)
            {
                throw new IOException("the connection was reset");
            }

            var n = (int)Math.Min(count, Math.Min(data.Length, until) - _at);
            if (n <= 0)
            {
                return 0;
            }

            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
