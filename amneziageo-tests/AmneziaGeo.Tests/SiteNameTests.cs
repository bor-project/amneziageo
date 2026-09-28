using System.Net.Security;
using System.Text;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The name of the site is read from the first flight a program sends: a TLS hello as a real client writes it, or
/// the head of an HTTP request.
/// </summary>
public sealed class SiteNameTests
{
    [Fact]
    public async Task TlsHelloNamesTheServer()
    {
        var hello = await HelloAsync("www.instagram.com");

        Assert.Equal(SiteName.Reading.Found, SiteName.Read(hello, out var name));
        Assert.Equal("www.instagram.com", name);
    }

    [Fact]
    public async Task TlsHelloCutShortAsksForMore()
    {
        var hello = await HelloAsync("rutracker.org");

        Assert.Equal(SiteName.Reading.More, SiteName.Read(hello.AsSpan(0, 3), out _));
        Assert.Equal(SiteName.Reading.More, SiteName.Read(hello.AsSpan(0, hello.Length - 1), out _));
    }

    [Fact]
    public async Task TlsHelloWithoutServerNameNamesNothing()
    {
        var hello = await HelloAsync("203.0.113.5");

        Assert.Equal(SiteName.Reading.None, SiteName.Read(hello, out var name));
        Assert.Null(name);
    }

    [Fact]
    public void HttpRequestNamesItsHost()
    {
        var request = "GET / HTTP/1.1\r\nUser-Agent: test\r\nHost: Rutracker.org:80\r\nAccept: */*\r\n\r\n"u8;

        Assert.Equal(SiteName.Reading.Found, SiteName.Read(request, out var name));
        Assert.Equal("rutracker.org", name);
    }

    [Fact]
    public void HttpRequestCutShortAsksForMore()
    {
        Assert.Equal(SiteName.Reading.More, SiteName.Read("GE"u8, out _));
        Assert.Equal(SiteName.Reading.More, SiteName.Read("GET / HTTP/1.1\r\nHo"u8, out _));
    }

    [Fact]
    public void AddressOrOtherProtocolNamesNothing()
    {
        Assert.Equal(SiteName.Reading.None, SiteName.Read("GET / HTTP/1.1\r\nHost: 203.0.113.5\r\n\r\n"u8, out _));
        Assert.Equal(SiteName.Reading.None, SiteName.Read("SSH-2.0-OpenSSH_9.6\r\n"u8, out _));
        Assert.Equal(SiteName.Reading.None, SiteName.Read(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nAccept: */*\r\n\r\n"), out _));
    }

    // The first flight a TLS client writes for a server name.
    private static async Task<byte[]> HelloAsync(string server)
    {
        var capture = new FirstWrite();
        using var tls = new SslStream(capture, false, (_, _, _, _) => true);
        var handshake = tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = server });
        var hello = await capture.Written.Task.WaitAsync(TimeSpan.FromSeconds(10));
        capture.Close();
        await Assert.ThrowsAnyAsync<Exception>(() => handshake);
        return hello;
    }

    /// <summary>
    /// Keeps what a client writes first and ends every read, so the handshake goes no further.
    /// </summary>
    private sealed class FirstWrite : Stream
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<byte[]> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _ended.Task.Wait();
            throw new IOException("closed");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _ended.Task.WaitAsync(cancellationToken);
            throw new IOException("closed");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written.TrySetResult(buffer.AsSpan(offset, count).ToArray());
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written.TrySetResult(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Close()
        {
            _ended.TrySetResult();
            base.Close();
        }
    }
}
