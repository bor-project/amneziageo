using System.Net;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The far end of a connection a machine took in is no destination of its own: a route laid for it would take the
/// answers off the path the connection came by. The socket tables tell such a connection by the port it stands on,
/// which a listening socket holds.
/// </summary>
public sealed class SocketTablesTests
{
    // 10.223.1.2 listens on 7790 and holds a connection taken in from 198.18.10.5 and one dialed to 198.18.10.7.
    private static readonly string[] Streams =
    [
        "   0: 0201DF0A:1E6E 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 4142 1 0000000000000000 100 0 0 10 0",
        "   1: 0201DF0A:1E6E 050A12C6:C0E8 01 00000000:00000000 00:00000000 00000000     0        0 4150 1 0000000000000000 20 4 30 10 -1",
        "   2: 0201DF0A:9C40 070A12C6:1E61 01 00000000:00000000 00:00000000 00000000     0        0 4151 1 0000000000000000 20 4 30 10 -1",
    ];

    [Fact]
    public void Listeners_NameThePortsConnectionsAreTakenInOn()
    {
        Assert.Equal(new[] { 0x1E6E }, SocketTables.Listeners(Streams));
    }

    [Fact]
    public void Peers_TakeTheFarEndOfEveryConnectionWhereNoPortIsSetApart()
    {
        var peers = new HashSet<uint>();

        SocketTables.Peers(Streams, null, peers);

        Assert.Equal(new[] { Numeric("198.18.10.5"), Numeric("198.18.10.7") }, peers.Order());
    }

    [Fact]
    public void Peers_LeaveOutAConnectionTakenInOnAListeningPort()
    {
        var peers = new HashSet<uint>();

        SocketTables.Peers(Streams, SocketTables.Listeners(Streams), peers);

        Assert.Equal(new[] { Numeric("198.18.10.7") }, peers);
    }

    [Fact]
    public void Peers_ReadAnAddressOfTheFourthFamilyOnASocketOfTheSixth()
    {
        string[] lines =
        [
            "   0: 00000000000000000000000000000000:0016 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 911 1 0000000000000000 100 0 0 10 0",
            "   1: 0000000000000000FFFF00000201DF0A:0016 0000000000000000FFFF0000050A12C6:D2F0 01 00000000:00000000 02:000A7D2B 00000000     0        0 5120 4 0000000000000000 20 4 31 10 -1",
            "   2: 0000000000000000FFFF00000201DF0A:A1B2 0000000000000000FFFF0000070A12C6:01BB 01 00000000:00000000 02:000A7D2B 00000000  1000        0 5121 2 0000000000000000 20 4 31 10 -1",
            "   3: B80D0120000000000000000001000000:A1B3 B80D0120000000000000000002000000:01BB 01 00000000:00000000 02:000A7D2B 00000000  1000        0 5122 2 0000000000000000 20 4 31 10 -1",
        ];
        var peers = new HashSet<uint>();

        SocketTables.Peers(lines, SocketTables.Listeners(lines), peers);

        Assert.Equal(new[] { Numeric("198.18.10.7") }, peers);
    }

    [Fact]
    public void Peers_SkipALineThatNamesNoFarEnd()
    {
        string[] lines =
        [
            "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode",
            " 1234: 00000000:14E9 00000000:0000 07 00000000:00000000 00:00000000 00000000   101        0 2210 2 0000000000000000 0",
            "",
        ];
        var peers = new HashSet<uint>();

        SocketTables.Peers(lines, null, peers);

        Assert.Empty(peers);
    }

    private static uint Numeric(string address)
    {
        Assert.True(GeoIpRanges.TryToNumeric(IPAddress.Parse(address), out var value));

        return value;
    }
}
