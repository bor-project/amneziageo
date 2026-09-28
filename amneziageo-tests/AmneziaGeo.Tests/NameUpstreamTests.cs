using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A filter on port 53 answers a name with a refusal, and the machine is then one step from having no names at
/// all. Every ladder here is one a corporate client actually put the resolver through.
/// </summary>
public sealed class NameUpstreamTests
{
    private static readonly byte[] _query = [0x00, 0x01];
    private static readonly byte[] _answer = [0x00, 0x02];
    // A reply header with the refused response code, as a filter on port 53 answers in place of the resolver.
    private static readonly byte[] _refusal = [0x00, 0x01, 0x81, 0x85, 0, 1, 0, 0, 0, 0, 0, 0];

    [Fact]
    public async Task PlainOnly_NeverReachesForTheOtherTransports()
    {
        var reached = 0;
        var upstream = new NameUpstream(DnsTransports.Plain,
            (_, _) => Task.FromResult(_answer),
            (_, _) => { reached++; return Task.FromResult(_answer); },
            (_, _) => { reached++; return Task.FromResult(_answer); });

        Assert.Equal(_answer, await upstream.AskAsync(_query));
        Assert.Equal(0, reached);
        Assert.Equal(NameUpstream.OnPlain, upstream.Carrying);
    }

    [Fact]
    public async Task PlainRefused_TakesTheFramedTransportAndSaysWhy()
    {
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => Task.FromException<byte[]>(new IOException("query refused")),
            (_, _) => Task.FromResult(_answer),
            (_, _) => Task.FromResult(_answer));

        Assert.Equal(_answer, await upstream.AskAsync(_query));
        Assert.Equal(NameUpstream.OnStream, upstream.Carrying);
        Assert.Contains("query refused", upstream.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainAnsweredWithARefusal_TakesTheFramedTransportAndSaysWhy()
    {
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => Task.FromResult(_refusal),
            (_, _) => Task.FromResult(_answer),
            (_, _) => Task.FromResult(_answer));

        Assert.Equal(_answer, await upstream.AskAsync(_query));
        Assert.Equal(NameUpstream.OnStream, upstream.Carrying);
        Assert.Contains("query refused", upstream.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryAnswerOnPort53ARefusal_EndsOnTheEncryptedOne()
    {
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => Task.FromResult(_refusal),
            (_, _) => Task.FromResult(_refusal),
            (_, _) => Task.FromResult(_answer));

        Assert.Equal(_answer, await upstream.AskAsync(_query));
        Assert.Equal(NameUpstream.OnEncrypted, upstream.Carrying);
        Assert.Contains($"{NameUpstream.OnStream} query refused", upstream.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKeptTransportThatStartsRefusing_IsLeftForTheLadder()
    {
        var streamRefuses = false;
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => Task.FromResult(_refusal),
            (_, _) => Task.FromResult(streamRefuses ? _refusal : _answer),
            (_, _) => Task.FromResult(_answer));

        await upstream.AskAsync(_query);
        streamRefuses = true;

        Assert.Equal(_answer, await upstream.AskAsync(_query));
        Assert.Equal(NameUpstream.OnEncrypted, upstream.Carrying);
    }

    [Fact]
    public async Task PlainOnly_HandsARefusalOnAsItCame()
    {
        var upstream = new NameUpstream(DnsTransports.Plain, (_, _) => Task.FromResult(_refusal));

        Assert.Equal(_refusal, await upstream.AskAsync(_query));
    }

    [Fact]
    public async Task EveryTransportOnPort53Refused_EndsOnTheEncryptedOne()
    {
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => Task.FromException<byte[]>(new IOException("query refused")),
            (_, _) => Task.FromException<byte[]>(new IOException("connection reset")),
            (_, _) => Task.FromResult(_answer));

        Assert.Equal(_answer, await upstream.AskAsync(_query));
        Assert.Equal(NameUpstream.OnEncrypted, upstream.Carrying);
    }

    [Fact]
    public async Task TheTransportThatAnswered_IsKeptForTheNextQuery()
    {
        var plainTries = 0;
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => { plainTries++; return Task.FromException<byte[]>(new IOException("query refused")); },
            null,
            (_, _) => Task.FromResult(_answer));

        await upstream.AskAsync(_query);
        await upstream.AskAsync(_query);
        await upstream.AskAsync(_query);

        Assert.Equal(1, plainTries);
        Assert.Equal(NameUpstream.OnEncrypted, upstream.Carrying);
    }

    [Fact]
    public async Task AResetAfterTheNetworkChanged_StartsAtPlainAgain()
    {
        var plainAnswers = false;
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => plainAnswers ? Task.FromResult(_answer) : Task.FromException<byte[]>(new IOException("query refused")),
            null,
            (_, _) => Task.FromResult(_answer));

        await upstream.AskAsync(_query);
        Assert.Equal(NameUpstream.OnEncrypted, upstream.Carrying);

        plainAnswers = true;
        upstream.Reset();
        await upstream.AskAsync(_query);

        Assert.Equal(NameUpstream.OnPlain, upstream.Carrying);
    }

    [Fact]
    public async Task NoTransportLeft_FailsWithWhatTheLastOneSaid()
    {
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => Task.FromException<byte[]>(new IOException("query refused")),
            (_, _) => Task.FromException<byte[]>(new IOException("connection reset")),
            (_, _) => Task.FromException<byte[]>(new IOException("443 timed out")));

        var thrown = await Assert.ThrowsAsync<IOException>(() => upstream.AskAsync(_query));

        Assert.Contains("443 timed out", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DohOnly_AsksNothingOnPort53()
    {
        var reached = 0;
        var upstream = new NameUpstream(DnsTransports.Doh,
            (_, _) => { reached++; return Task.FromResult(_answer); },
            (_, _) => { reached++; return Task.FromResult(_answer); },
            (_, _) => Task.FromResult(_answer));

        Assert.Equal(_answer, await upstream.AskAsync(_query));
        Assert.Equal(0, reached);
        Assert.Equal(NameUpstream.OnEncrypted, upstream.Carrying);
    }

    [Fact]
    public void ATransportNobodyNames_FallsBackToAuto()
    {
        Assert.Equal(DnsTransports.Auto, DnsTransports.Of("tls"));
        Assert.Equal(DnsTransports.Doh, DnsTransports.Of(" DoH "));
        Assert.False(DnsTransports.IsKnown("tls"));
    }
    [Fact]
    public async Task TheReasonNamesTheTransportThatGaveWay()
    {
        var upstream = new NameUpstream(DnsTransports.Auto,
            (_, _) => throw new IOException("query refused"),
            (_, _) => throw new IOException("connection reset"),
            (_, _) => Task.FromResult(_answer));

        Assert.Equal(_answer, await upstream.AskAsync(_query));
        Assert.Equal(NameUpstream.OnEncrypted, upstream.Carrying);
        Assert.Contains($"{NameUpstream.OnStream} connection reset", upstream.State, StringComparison.Ordinal);
    }
}
