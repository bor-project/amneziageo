using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The session list of the window says in a word what settled each destination; an address the all-UDP mode sent
/// into the tunnel names that mode.
/// </summary>
public sealed class SessionWhyTests
{
    [Fact]
    public void AnAddressAllUdpSentIntoTheTunnel_NamesTheMode()
    {
        var card = Card(LiveSession.ReasonUdp);

        Assert.Equal(Loc.Instance.Get("Check_Verdict_proxy"), card.WayText);
        Assert.Equal(Loc.Instance.Get("Check_Why_udp"), card.Why);
        Assert.NotEqual(Loc.Instance.Get("Check_Why_none"), card.Why);
    }

    [Fact]
    public void TheWordOfTheMode_IsTranslated()
    {
        Assert.Equal("all UDP", Loc.GetIn("en", "Check_Why_udp"));
        Assert.Equal("весь UDP", Loc.GetIn("ru", "Check_Why_udp"));
    }

    [Fact]
    public void TheCopiedRow_CarriesTheWordOfTheMode()
    {
        var card = Card(LiveSession.ReasonUdp);

        Assert.Contains(Loc.Instance.Get("Check_Why_udp"), card.Line);
        Assert.Contains(Loc.Instance.Get("Check_Why_udp"), SessionRows.Text([card]));
        Assert.DoesNotContain(Loc.Instance.Get("Check_Why_none"), card.Line);
    }

    [Theory]
    [InlineData(LiveSession.ReasonRange, "Check_Why_range")]
    [InlineData(LiveSession.ReasonName, "Check_Why_name")]
    [InlineData(LiveSession.ReasonApp, "Check_Why_app")]
    [InlineData(LiveSession.ReasonResolved, "Check_Why_resolved")]
    [InlineData(LiveSession.ReasonService, "Check_Why_service")]
    [InlineData(LiveSession.ReasonConfig, "Check_Why_config")]
    [InlineData(LiveSession.ReasonNone, "Check_Why_none")]
    [InlineData("", "Check_Why_none")]
    public void TheOtherReasons_KeepTheirWords(string reason, string key)
    {
        Assert.Equal(Loc.Instance.Get(key), Card(reason).Why);
    }

    private static LiveRowItem Card(string reason)
    {
        var row = new LiveSession("198.51.100.7", LiveSession.Undecided, Path: LiveSession.PathTunnel, Reason: reason);
        return Assert.Single(SessionRows.Cards(new SessionReport(0, [row], Held: 1, Tunnel: 1)));
    }
}
