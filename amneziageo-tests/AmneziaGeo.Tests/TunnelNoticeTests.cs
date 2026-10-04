using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The notification of the tunnel: the stage with the speed or the attempt, the routing list, and the guard that
/// gives up a tunnel which keeps dying.
/// </summary>
public sealed class TunnelNoticeTests
{
    private static readonly NoticeWords _ru = new(
        "Подключено", "Подключение", "попытка {0}", "Отключено", "Туннель остановлен", "↓ {0} ↑ {1} Мбит/с",
        "↓ {0} ↑ {1} кбит/с", "Маршрутизация: {0}", "Маршрутизация выключена", "Отключить", "Подключить",
        "Выключить список", "Включить список", "Туннель остановлен", "ru-RU");

    [Fact]
    public void AStandingSession_ShowsItsSpeedInTheUnitOfTheFasterDirection()
    {
        Assert.Equal("Подключено · ↓ 12,4 ↑ 0,3 Мбит/с", TunnelNotice.Text(_ru, NoticeStage.Connected, 0, 12_400_000, 300_000));
        Assert.Equal("Подключено · ↓ 840 ↑ 12 кбит/с", TunnelNotice.Text(_ru, NoticeStage.Connected, 0, 840_000, 12_500));
        Assert.Equal("Подключено · ↓ 0 ↑ 0 кбит/с", TunnelNotice.Text(_ru, NoticeStage.Connected, 0, 0, 0));
    }

    [Fact]
    public void ASessionBeingRaised_NamesTheAttemptFromTheSecondOne()
    {
        Assert.Equal("Подключение", TunnelNotice.Text(_ru, NoticeStage.Connecting, 0, 0, 0));
        Assert.Equal("Подключение · попытка 2", TunnelNotice.Text(_ru, NoticeStage.Connecting, 1, 0, 0));
        Assert.Equal("Подключение · попытка 5", TunnelNotice.Text(_ru, NoticeStage.Connecting, 4, 0, 0));
    }

    [Fact]
    public void ATunnelThatIsDown_SaysWhoTookItDown()
    {
        Assert.Equal("Отключено", TunnelNotice.Text(_ru, NoticeStage.Disconnected, 3, 5_000_000, 5_000_000));
        Assert.Equal("Туннель остановлен", TunnelNotice.Text(_ru, NoticeStage.Stopped, 0, 0, 0));
    }

    [Fact]
    public void TheWordsOfATunnelWithoutAHead_WriteNumbersTheSameWayEverywhere()
    {
        Assert.Equal("Connected · ↓ 1.5 ↑ 0.2 Mbit/s", TunnelNotice.Text(NoticeWords.Plain, NoticeStage.Connected, 0, 1_500_000, 200_000));
        Assert.Equal("Connected · ↓ 1.5 ↑ 0.2 Mbit/s",
            TunnelNotice.Text(NoticeWords.Plain with { Culture = "no-such-culture-name" }, NoticeStage.Connected, 0, 1_500_000, 200_000));
    }

    [Fact]
    public void AListInUse_IsNamedAndCanBeTurnedOff()
    {
        var words = _ru with { List = "Недоступные сайты", Offer = 7 };

        Assert.Equal("Маршрутизация: Недоступные сайты", TunnelNotice.Routing(words));
        Assert.Equal("Выключить список", TunnelNotice.ListAction(words));
    }

    [Fact]
    public void RoutingThatIsOff_CanBeTurnedOnWhileThereIsAList()
    {
        var words = _ru with { Offer = 7 };

        Assert.Equal("Маршрутизация выключена", TunnelNotice.Routing(words));
        Assert.Equal("Включить список", TunnelNotice.ListAction(words));
    }

    [Fact]
    public void WithoutLists_NothingIsSaidAboutRouting()
    {
        Assert.Null(TunnelNotice.Routing(_ru));
        Assert.Null(TunnelNotice.ListAction(_ru));
    }

    [Fact]
    public void TheGuard_GivesUpAfterItsRaisesInARow()
    {
        var kept = default(string);
        for (var raise = 0; raise < GuardTally.Limit; raise++)
        {
            Assert.False(GuardTally.Spent(kept));
            kept = GuardTally.Next(kept);
        }

        Assert.True(GuardTally.Spent(kept));
        Assert.False(GuardTally.Spent("not a number"));
        Assert.False(GuardTally.Spent(string.Empty));
    }
}
