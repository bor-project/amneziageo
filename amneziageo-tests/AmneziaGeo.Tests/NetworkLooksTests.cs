using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The tunnel service reads the networks of the device at a look at the link only when the system told of a change
/// in what a read takes from them, while it waits for the tunnel to be validated, and once in twelve looks whatever
/// was told.
/// </summary>
public sealed class NetworkLooksTests
{
    private const long Wifi = 7;

    [Fact]
    public void TheFirstLook_Reads()
    {
        Assert.True(new NetworkLooks().Due(false));
    }

    [Fact]
    public void QuietLooks_DoNotRead_UntilTheTwelfth()
    {
        var looks = Read();

        var read = Enumerable.Range(0, 24).Where(_ => looks.Due(false)).ToArray();

        Assert.Equal([11, 23], read);
    }

    [Fact]
    public void AChange_IsReadAtTheNextLookAndOnlyThere()
    {
        var looks = Read();

        looks.Changed();
        looks.Changed();

        Assert.True(looks.Due(false));
        Assert.False(looks.Due(false));
    }

    [Fact]
    public void AReadAfterAChange_StartsTheCountAgain()
    {
        var looks = Read();
        for (var i = 0; i < 5; i++)
        {
            looks.Due(false);
        }

        looks.Changed();
        looks.Due(false);
        var read = Enumerable.Range(0, 12).Where(_ => looks.Due(false)).ToArray();

        Assert.Equal([11], read);
    }

    [Fact]
    public void WhileTheCallerWaits_EveryLookReads()
    {
        var looks = Read();

        Assert.True(looks.Due(true));
        Assert.True(looks.Due(true));
        Assert.False(looks.Due(false));
    }

    [Fact]
    public void WhatANetworkCanDo_IsAChangeOnlyWhenItDiffers()
    {
        var looks = Read();

        looks.Noted(Wifi, false, 3);
        Assert.True(looks.Due(false));

        looks.Noted(Wifi, false, 3);
        Assert.False(looks.Due(false));

        looks.Noted(Wifi, false, 1);
        Assert.True(looks.Due(false));
    }

    [Fact]
    public void TheLinkOfANetwork_IsNotedApartFromWhatItCanDo()
    {
        var looks = Read();
        looks.Noted(Wifi, false, 3);
        looks.Due(false);

        looks.Noted(Wifi, true, 3);
        Assert.True(looks.Due(false));

        looks.Noted(Wifi, true, 3);
        looks.Noted(Wifi, false, 3);
        Assert.False(looks.Due(false));
    }

    [Fact]
    public void ANetworkThatIsGone_IsAChangeAndIsNotedAnewWhenItComesBack()
    {
        var looks = Read();
        looks.Noted(Wifi, false, 3);
        looks.Due(false);

        looks.Gone(Wifi);
        Assert.True(looks.Due(false));

        looks.Noted(Wifi, false, 3);
        Assert.True(looks.Due(false));
    }

    // The looks after the first read.
    private static NetworkLooks Read()
    {
        var looks = new NetworkLooks();
        looks.Due(false);
        return looks;
    }
}
