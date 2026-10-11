using System.Diagnostics;

using AmneziaGeo.Windows.App;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An exchange with a device waits for its pipe only while the device may still come up: once the device is told
/// closed, a wait under way ends and a new one does not begin.
/// </summary>
public sealed class UapiClosedDeviceTests
{
    private static readonly string _peer = Convert.ToBase64String(new byte[32]);

    [Fact]
    public async Task AWaitForThePipe_EndsWhenTheDeviceIsToldClosed()
    {
        using var uapi = new UapiClient(NullLogger<UapiClient>.Instance);
        var waiting = Task.Run(() => uapi.TryGetLastHandshake("t259-wait"));
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        uapi.Close("t259-wait");

        Assert.Same(waiting, await Task.WhenAny(waiting, Task.Delay(2000)));
        Assert.Null(await waiting);
    }

    [Fact]
    public void AnExchangeWithAClosedDevice_DoesNotWait()
    {
        using var uapi = new UapiClient(NullLogger<UapiClient>.Instance);
        uapi.Close("t259-closed");
        var watch = Stopwatch.StartNew();

        Assert.Null(uapi.TryGetLastHandshake("t259-closed"));
        Assert.Throws<TimeoutException>(() => uapi.AddAllowedIps("t259-closed", _peer, ["192.0.2.7/32"]));

        Assert.InRange(watch.ElapsedMilliseconds, 0, 1500);
    }

    [Fact]
    public async Task ADeviceComingUpAgain_IsWaitedForAgain()
    {
        using var uapi = new UapiClient(NullLogger<UapiClient>.Instance);
        uapi.Close("t259-again");
        uapi.Open("t259-again");

        var waiting = Task.Run(() => uapi.TryGetLastHandshake("t259-again"));
        await Task.Delay(300);
        var held = !waiting.IsCompleted;
        uapi.Close("t259-again");
        await Task.WhenAny(waiting, Task.Delay(6000));

        Assert.True(held);
    }
}
