using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The choice of the tunnel notification under the row of notifications: what the user picks goes to the agent,
/// what the agent tells only shows, and a new language keeps the pick.
/// </summary>
public sealed class TunnelNoticeChoiceTests
{
    [Fact]
    public void APick_GoesToTheAgent()
    {
        var pushed = new List<string>();
        var choice = new TunnelNoticeChoice(pushed.Add);

        choice.SelectedIndex = 1;
        choice.SelectedIndex = 0;

        Assert.Equal([TunnelNoticeModes.Brief, TunnelNoticeModes.Full], pushed);
    }

    [Fact]
    public void WhatTheAgentTells_ShowsWithoutGoingBack()
    {
        var pushed = new List<string>();
        var choice = new TunnelNoticeChoice(pushed.Add);

        choice.Apply(TunnelNoticeModes.Brief);
        Assert.Equal(1, choice.SelectedIndex);

        choice.Apply(string.Empty);
        Assert.Equal(0, choice.SelectedIndex);
        Assert.Empty(pushed);
    }

    [Fact]
    public void ANewLanguage_KeepsThePick()
    {
        var pushed = new List<string>();
        var choice = new TunnelNoticeChoice(pushed.Add);
        choice.Apply(TunnelNoticeModes.Brief);

        choice.Relabel();

        Assert.Equal(1, choice.SelectedIndex);
        Assert.Equal([Loc.Instance.Get("Background_TunnelNoticeFull"), Loc.Instance.Get("Background_TunnelNoticeBrief")], choice.Options);
        Assert.Empty(pushed);
    }

    [Fact]
    public void TheRowOfNotifications_CarriesTheChoice()
    {
        var choice = new TunnelNoticeChoice(_ => { });
        var block = new BackgroundLimitsViewModel(
            () =>
            [
                new BackgroundLimit(BackgroundLimitKind.Notifications, BackgroundLimitState.Free),
                new BackgroundLimit(BackgroundLimitKind.Battery, BackgroundLimitState.Limited),
            ],
            _ => { },
            choice);

        Assert.Same(choice, block.Rows[0].Notice);
        Assert.True(block.Rows[0].HasNotice);
        Assert.Null(block.Rows[1].Notice);
        Assert.False(block.Rows[1].HasNotice);
    }

    [Fact]
    public void ABlockWithoutTheChoice_HasNoneUnderItsRows()
    {
        var block = new BackgroundLimitsViewModel(
            () => [new BackgroundLimit(BackgroundLimitKind.Notifications, BackgroundLimitState.Free)],
            _ => { });

        Assert.False(Assert.Single(block.Rows).HasNotice);
    }

    [Theory]
    [InlineData("Background_TunnelNotice")]
    [InlineData("Background_TunnelNoticeFull")]
    [InlineData("Background_TunnelNoticeBrief")]
    [InlineData("Android_UpdateFound")]
    [InlineData("Android_UpdateFoundChannel")]
    public void EveryTextOfTheChoice_IsTranslated(string key)
    {
        var english = Loc.GetIn("en", key);
        var russian = Loc.GetIn("ru", key);

        Assert.NotEqual(key, english);
        Assert.NotEqual(key, russian);
        Assert.NotEqual(english, russian);
    }
}
