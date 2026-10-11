using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A websocket front that answers the upgrade with anything but the switch makes wstunnel ask again at once and
/// without end. The carrier is stopped on the first refusal it writes, what the front answered is read out of the
/// lines under it, and the wait before the next start grows while the refusals come in a row.
/// </summary>
public sealed class CarrierRefusalTests
{
    private const string Started = "2026-10-11T12:50:31.118402Z INFO wstunnel: Starting wstunnel client v10.5.5";
    private const string Dialled = "2026-10-11T12:50:38.012551Z INFO wstunnel::protocols::tcp::server: Opening TCP connection to 203.0.113.7:51231";
    private const string Failed = "2026-10-11T12:50:38.040679Z ERROR tunnel{id=\"01a12b04-2aa7-7cb0-8e1d-ff528a8fa59b\" remote=\"127.0.0.1:51231\"}: wstunnel::tunnel::client::client: failed to do websocket handshake with the server wss://203.0.113.7:51231";

    // The same line as wstunnel writes it into a pipe on Windows, colours and all.
    private const string FailedInColour = "\u001b[2m2026-10-11T12:50:38.040679Z\u001b[0m \u001b[31mERROR\u001b[0m \u001b[1mtunnel\u001b[0m\u001b[1m{\u001b[0m\u001b[3mid\u001b[0m\u001b[2m=\u001b[0m\"01a12b04-2aa7-7cb0-8e1d-ff528a8fa59b\" \u001b[3mremote\u001b[0m\u001b[2m=\u001b[0m\"127.0.0.1:51231\"\u001b[1m}\u001b[0m\u001b[2m:\u001b[0m \u001b[2mwstunnel::tunnel::client::client\u001b[0m\u001b[2m:\u001b[0m failed to do websocket handshake with the server wss://203.0.113.7:51231";

    [Fact]
    public void LinesOfACarrierThatIsLetIn_ArePlain()
    {
        var said = new CarrierRefusal();

        Assert.Equal(CarrierLine.Plain, said.Take(Started));
        Assert.Equal(CarrierLine.Plain, said.Take(Dialled));
        Assert.False(said.Refused);
        Assert.Equal(string.Empty, said.Cause);
    }

    [Fact]
    public void TheFirstRefusalIsTold_AndEveryLineAfterItIsNot()
    {
        var said = new CarrierRefusal();
        said.Take(Started);

        Assert.Equal(CarrierLine.Refused, said.Take(Failed));
        Assert.True(said.Refused);
        Assert.Equal(CarrierLine.After, said.Take(string.Empty));
        Assert.Equal(CarrierLine.After, said.Take(Dialled));
        Assert.Equal(CarrierLine.After, said.Take(Failed));
    }

    [Theory]
    [InlineData("    Invalid status code: 404", "HTTP 404")]
    [InlineData("    Invalid status code: 502", "HTTP 502")]
    [InlineData("    invalid HTTP version parsed", "invalid HTTP version parsed")]
    public void WhatTheFrontAnswered_IsReadFromUnderTheRefusal(string under, string cause)
    {
        var said = new CarrierRefusal();
        said.Take(Failed);
        said.Take(string.Empty);
        said.Take("Caused by:");
        said.Take(under);

        Assert.Equal(cause, said.Cause);
    }

    [Fact]
    public void AConnectionClosedWithoutAnAnswer_IsNamedByTheLastOfItsCauses()
    {
        var said = new CarrierRefusal();
        said.Take(Failed);
        said.Take(string.Empty);
        said.Take("Caused by:");
        said.Take("    0: operation was canceled");
        said.Take("    1: connection closed before message completed");

        Assert.Equal("connection closed before message completed", said.Cause);
    }

    [Fact]
    public void TheNextAttemptOfTheProcess_DoesNotChangeTheCause()
    {
        var said = new CarrierRefusal();
        said.Take(Failed);
        said.Take(string.Empty);
        said.Take("Caused by:");
        said.Take("    Invalid status code: 404");
        said.Take(Dialled);
        said.Take(Failed);
        said.Take(string.Empty);
        said.Take("Caused by:");
        said.Take("    Invalid status code: 502");

        Assert.Equal("HTTP 404", said.Cause);
    }

    [Fact]
    public void ALineInColour_IsReadLikeAPlainOne()
    {
        var said = new CarrierRefusal();

        Assert.Equal(CarrierLine.Refused, said.Take(FailedInColour));
        said.Take("\u001b[0m");
        said.Take("Caused by:");
        said.Take("    Invalid status code: 404\u001b[0m");

        Assert.Equal("HTTP 404", said.Cause);
    }

    [Fact]
    public void ACertificateThatIsNotTaken_IsNoRefusalOfTheFront()
    {
        var said = new CarrierRefusal();

        Assert.Equal(CarrierLine.Plain, said.Take("2026-10-11T12:50:38.040679Z ERROR wstunnel::protocols::tls: invalid peer certificate: UnknownIssuer"));
        Assert.False(said.Refused);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 5)]
    [InlineData(7, 8)]
    [InlineData(8, 15)]
    [InlineData(9, 30)]
    [InlineData(10, 30)]
    [InlineData(500, 30)]
    public void TheWaitGrowsWithTheRefusalsInARow_UpToHalfAMinute(int inRow, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RefusalPauses.Of(inRow));
    }

    [Fact]
    public void CarriersRefusedOneAfterAnother_WaitLongerEachTime()
    {
        var pauses = new RefusalPauses();

        var waits = Enumerable.Range(0, 7).Select(_ => (int)pauses.Next(carried: false, livedMs: 300).TotalSeconds).ToArray();

        Assert.Equal([1, 1, 1, 2, 3, 5, 8], waits);
        Assert.Equal(7, pauses.InRow);
    }

    [Fact]
    public void ACarrierThatCarriedBeforeItWasRefused_StartsTheCountAnew()
    {
        var pauses = new RefusalPauses();
        pauses.Next(carried: false, livedMs: 300);
        pauses.Next(carried: false, livedMs: 300);
        pauses.Next(carried: false, livedMs: 300);
        pauses.Next(carried: false, livedMs: 300);

        Assert.Equal(TimeSpan.FromSeconds(3), pauses.Next(carried: false, livedMs: 300));
        Assert.Equal(TimeSpan.FromSeconds(1), pauses.Next(carried: true, livedMs: 9000));
        Assert.Equal(1, pauses.InRow);
    }

    [Fact]
    public void ACarrierThatLivedLongBeforeItWasRefused_StartsTheCountAnew()
    {
        var pauses = new RefusalPauses();
        pauses.Next(carried: false, livedMs: 300);
        pauses.Next(carried: false, livedMs: 300);
        pauses.Next(carried: false, livedMs: 300);
        pauses.Next(carried: false, livedMs: 300);

        Assert.Equal(TimeSpan.FromSeconds(1), pauses.Next(carried: false, livedMs: RefusalPauses.SettledMs));
        Assert.Equal(1, pauses.InRow);
        pauses.Next(carried: false, livedMs: RefusalPauses.SettledMs - 1);
        pauses.Next(carried: false, livedMs: RefusalPauses.SettledMs - 1);
        Assert.Equal(TimeSpan.FromSeconds(2), pauses.Next(carried: false, livedMs: RefusalPauses.SettledMs - 1));
        Assert.Equal(4, pauses.InRow);
    }
}
