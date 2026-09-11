using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An install the window starts holds the downloaded update until its setup ends, so nothing offers it twice.
/// </summary>
public sealed class UpdateStateTests
{
    [Fact]
    public void AnInstallingSetup_StaysReadyForItsVersionOnly()
    {
        var state = Installing(42);

        Assert.True(state.ReadyFor("1.8.10.0"));
        Assert.False(state.ReadyFor("1.8.11.0"));
    }

    [Fact]
    public void TheEndOfItsSetup_ReturnsTheUpdateToDownloaded()
    {
        var state = Installing(42);

        Assert.True(state.EndInstall(42));
        Assert.Equal(UpdateDownloadPhase.Downloaded, state.DownloadPhase);
        Assert.Equal(0, state.InstallerPid);
    }

    [Fact]
    public void TheEndOfAnEarlierSetup_LeavesALaterPhase()
    {
        var state = Installing(42);
        state.DownloadPhase = UpdateDownloadPhase.Downloading;
        state.InstallerPid = 0;

        Assert.False(state.EndInstall(42));
        Assert.Equal(UpdateDownloadPhase.Downloading, state.DownloadPhase);
    }

    // A state whose setup for 1.8.10.0 runs as the given process.
    private static UpdateState Installing(int pid)
    {
        return new UpdateState
        {
            DownloadPhase = UpdateDownloadPhase.Installing,
            DownloadedVersion = "1.8.10.0",
            DownloadedSetupPath = @"C:\Temp\AmneziaGeoSetup.exe",
            InstallerPid = pid,
        };
    }
}
