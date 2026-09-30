using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An answer is kept, here and by the system, no longer than the routes it puts in place.
/// </summary>
public sealed class DnsAnswerTtlTests
{
    private const int TypeCname = 5;
    private const int TypeOpt = 41;

    private static readonly GeoDomain YouTube = new(GeoDomainKind.Domain, "youtube.com");
    private static readonly IPAddress Resolver = IPAddress.Parse("10.8.1.1");
    private static readonly byte[] LentQuery = DnsMessage.BuildQuery("www.lent.example", 1);

    private int _asked;

    [Fact]
    public void CapTtl_CutsTheLongerRecords_AndLeavesTheRestAlone()
    {
        var answer = AliasAnswer();

        var capped = DnsMessage.CapTtl(answer, 300);

        Assert.Equal(new (int, uint)[] { (TypeCname, 300), (1, 60), (TypeOpt, 0x8000) }, Records(capped));
        Assert.Equal(new (int, uint)[] { (TypeCname, 86400), (1, 60), (TypeOpt, 0x8000) }, Records(answer));
        Assert.Equal(DnsMessage.Addresses(answer), DnsMessage.Addresses(capped));
    }

    [Fact]
    public void CapTtl_ToZero_LeavesNothingToKeep()
    {
        var capped = DnsMessage.CapTtl(AliasAnswer(), 0);

        Assert.Equal(new (int, uint)[] { (TypeCname, 0), (1, 0), (TypeOpt, 0x8000) }, Records(capped));
    }

    [Fact]
    public void CapTtl_LeavesAnAnswerWithoutRecordsAsItIs()
    {
        var empty = DnsMessage.BuildNxDomain(LentQuery);

        Assert.Equal(empty, DnsMessage.CapTtl(empty, 10));
        Assert.Equal(new byte[] { 1, 2, 3 }, DnsMessage.CapTtl([1, 2, 3], 10));
    }

    [Fact]
    public async Task AnAnswer_IsKeptByTheSystemNoLongerThanTheRoutes()
    {
        var proxy = Lending();
        proxy.SetRouteTtl(10);

        var answer = await proxy.AnswerAsync(LentQuery, CancellationToken.None);

        Assert.Equal(new[] { IPAddress.Parse("192.0.2.7") }, DnsMessage.Addresses(answer!));
        Assert.Equal(10, DnsMessage.MinTtl(answer!));
    }

    [Fact]
    public async Task WithoutALifetime_NothingIsCached()
    {
        var proxy = Lending();
        proxy.SetRouteTtl(0);

        await proxy.AnswerAsync(LentQuery, CancellationToken.None);
        var second = await proxy.AnswerAsync(LentQuery, CancellationToken.None);

        Assert.Equal(2, _asked);
        Assert.Equal(new[] { IPAddress.Parse("192.0.2.7") }, DnsMessage.Addresses(second!));
        Assert.All(Records(second!), record => Assert.Equal(0u, record.Ttl));
    }

    [Fact]
    public async Task ACachedAnswer_CarriesWhatIsLeftOfItsLife()
    {
        var proxy = Lending();
        proxy.SetRouteTtl(300);

        var first = await proxy.AnswerAsync(LentQuery, CancellationToken.None);
        await Task.Delay(1100);
        var second = await proxy.AnswerAsync(LentQuery, CancellationToken.None);

        Assert.Equal(1, _asked);
        Assert.Equal(30, DnsMessage.MinTtl(first!));
        Assert.InRange(DnsMessage.MinTtl(second!), 1, 29);
    }

    [Fact]
    public async Task ANewLifetime_DropsWhatTheCacheHolds()
    {
        var proxy = Lending();
        proxy.SetRouteTtl(300);
        await proxy.AnswerAsync(LentQuery, CancellationToken.None);

        proxy.SetRouteTtl(5);
        var answer = await proxy.AnswerAsync(LentQuery, CancellationToken.None);

        Assert.Equal(2, _asked);
        Assert.Equal(5, DnsMessage.MinTtl(answer!));
    }

    // A proxy handing the names of lent.example to another tunnel of the set, which answers without any resolver.
    private DnsProxy Lending()
    {
        var proxy = new DnsProxy([YouTube], [], Resolver, Resolver, null, [], true, [], [], null, NullLogger<DnsProxy>.Instance, stripV6: false, listen: false);
        proxy.SetLentNames(
            name => name.EndsWith("lent.example", StringComparison.Ordinal) ? "second" : null,
            (_, _) =>
            {
                Interlocked.Increment(ref _asked);
                return Task.FromResult<IReadOnlyList<string>>(["192.0.2.7"]);
            });
        return proxy;
    }

    // An answer to www.example.com: an alias kept for a day, its address for a minute, and the EDNS record whose TTL
    // field carries flags.
    private static byte[] AliasAnswer()
    {
        var bytes = new List<byte>(DnsMessage.BuildQuery("www.example.com", 1));
        bytes[2] = 0x81;
        bytes[3] = 0x80;
        bytes[7] = 2;
        bytes[11] = 1;
        var target = new List<byte>();
        foreach (var label in "edge.example.net".Split('.'))
        {
            target.Add((byte)label.Length);
            target.AddRange(label.Select(c => (byte)c));
        }

        target.Add(0);
        bytes.AddRange([0xC0, 0x0C, 0x00, 0x05, 0x00, 0x01, 0x00, 0x01, 0x51, 0x80, 0x00, (byte)target.Count]);
        var targetOffset = bytes.Count;
        bytes.AddRange(target);
        bytes.AddRange([0xC0, (byte)targetOffset, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04, 192, 0, 2, 1]);
        bytes.AddRange([0x00, 0x00, 0x29, 0x04, 0xD0, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00]);
        return [.. bytes];
    }

    // Type and TTL of every record past the question, in the order they stand.
    private static List<(int Type, uint Ttl)> Records(byte[] message)
    {
        var records = new List<(int Type, uint Ttl)>();
        var count = ((message[6] << 8) | message[7]) + ((message[8] << 8) | message[9]) + ((message[10] << 8) | message[11]);
        var offset = 12;
        SkipName(message, ref offset);
        offset += 4;
        for (var i = 0; i < count; i++)
        {
            SkipName(message, ref offset);
            var type = (message[offset] << 8) | message[offset + 1];
            var ttl = ((uint)message[offset + 4] << 24) | ((uint)message[offset + 5] << 16) | ((uint)message[offset + 6] << 8) | message[offset + 7];
            records.Add((type, ttl));
            offset += 10 + ((message[offset + 8] << 8) | message[offset + 9]);
        }

        return records;
    }

    private static void SkipName(byte[] message, ref int offset)
    {
        while (message[offset] != 0)
        {
            if ((message[offset] & 0xC0) == 0xC0)
            {
                offset += 2;
                return;
            }

            offset += 1 + message[offset];
        }

        offset++;
    }
}
