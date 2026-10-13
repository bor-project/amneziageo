using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The clients of the local proxy as the status shows them: one entry per address out of the open connections, and
/// the rows of text the tunnel process of android hands them to the window in.
/// </summary>
public sealed class ProxyClientEntryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Gather_GivesOneEntryPerAddressTheBusiestFirst()
    {
        var clients = ProxyClientEntry.Gather(
        [
            ("192.0.2.7", Noon.AddSeconds(30)),
            ("192.0.2.9", Noon.AddSeconds(5)),
            ("192.0.2.9", Noon),
            ("192.0.2.9", Noon.AddSeconds(40)),
            ("192.0.2.3", Noon.AddSeconds(10)),
        ]);

        Assert.Equal(
            [
                new ProxyClientEntry("192.0.2.9", string.Empty, 3, Noon),
                new ProxyClientEntry("192.0.2.3", string.Empty, 1, Noon.AddSeconds(10)),
                new ProxyClientEntry("192.0.2.7", string.Empty, 1, Noon.AddSeconds(30)),
            ],
            clients);
    }

    [Fact]
    public void Gather_GivesNothingWhenNobodyIsConnected()
    {
        Assert.Empty(ProxyClientEntry.Gather([]));
    }

    [Fact]
    public void Rows_ComeBackAsTheClientsTheyWereWrittenFrom()
    {
        ProxyClientEntry[] clients =
        [
            new("192.0.2.9", string.Empty, 3, Noon),
            new("127.0.0.1", "browser, mail", 12, Noon.AddMilliseconds(1500)),
        ];

        var back = ProxyClientEntry.FromRows(ProxyClientEntry.ToRows(clients).Split('\n'));

        Assert.Equal(clients, back);
    }

    [Fact]
    public void Rows_OfNobodyAreEmpty()
    {
        Assert.Equal(string.Empty, ProxyClientEntry.ToRows([]));
        Assert.Empty(ProxyClientEntry.FromRows([string.Empty]));
    }

    [Fact]
    public void Rows_KeepANameOnItsOwnRow()
    {
        ProxyClientEntry[] clients = [new("127.0.0.1", "one\ttwo\nthree", 1, Noon)];

        var back = ProxyClientEntry.FromRows(ProxyClientEntry.ToRows(clients).Split('\n'));

        Assert.Equal([new ProxyClientEntry("127.0.0.1", "one two three", 1, Noon)], back);
    }

    [Fact]
    public void FromRows_SkipsWhatIsNotAClient()
    {
        var back = ProxyClientEntry.FromRows(
            [string.Empty, "192.0.2.9", "192.0.2.9\tmany\t0", "192.0.2.9\t2\tthen", "\t2\t0", "192.0.2.9\t-2\t0", "192.0.2.9\t2\t1000"]);

        Assert.Equal([new ProxyClientEntry("192.0.2.9", string.Empty, 2, DateTimeOffset.FromUnixTimeMilliseconds(1000))], back);
    }
}
