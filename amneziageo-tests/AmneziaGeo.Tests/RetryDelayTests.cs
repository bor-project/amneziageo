using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The pause after each failed attempt: none after the first, then 5, 10 and 20 s, then the retry interval, which
/// also caps every step; a minute stands in for the interval while auto-reconnect is off.
/// </summary>
public sealed class RetryDelayTests
{
    [Theory]
    [InlineData(true, 30, new[] { 0, 5, 10, 20, 30, 30 })]
    [InlineData(true, 60, new[] { 0, 5, 10, 20, 60, 60 })]
    [InlineData(true, 10, new[] { 0, 5, 10, 10, 10 })]
    [InlineData(true, 3, new[] { 0, 3, 3 })]
    [InlineData(false, 30, new[] { 0, 5, 10, 20, 60, 60 })]
    [InlineData(true, 0, new[] { 0, 5, 10, 20, 60 })]
    public void EachFailure_WaitsItsStep(bool periodic, int interval, int[] expected)
    {
        var delays = Enumerable.Range(1, expected.Length)
            .Select(attempt => (int)ConnectRetry.Delay(attempt, periodic, interval).TotalSeconds)
            .ToArray();

        Assert.Equal(expected, delays);
    }
}
