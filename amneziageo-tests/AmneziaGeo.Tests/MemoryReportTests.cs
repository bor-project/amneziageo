using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The tunnel process writes one line about its memory: the process, the managed heap and the engine.
/// </summary>
public sealed class MemoryReportTests
{
    private const long Mib = 1024 * 1024;

    [Fact]
    public void TheLine_NamesTheProcessTheManagedHeapAndTheEngine()
    {
        var engine = $"{24 * Mib} {(5 * Mib) + (Mib / 2)} {3 * Mib} {40 * Mib} 31 12";

        var line = MemoryReport.Compose(212 * Mib, 45 * Mib, 67 * Mib, 120, 8, engine);

        Assert.Equal(
            "memory: process 212.0 MiB; managed 45.0 of 67.0 MiB, 120/8 collection(s); engine 24.0 MiB in use, "
            + "5.5 held free, 3.0 returned, 40.0 taken, 31 goroutine(s), 12 collection(s)",
            line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1 2 3")]
    [InlineData("1 2 3 4 5 six")]
    [InlineData("1 2 3 4 5 6 7")]
    public void AnEngineThatToldNothingUsable_IsNamedUnknown(string? engine)
    {
        var line = MemoryReport.Compose(Mib, Mib, Mib, 0, 0, engine);

        Assert.EndsWith("engine unknown", line);
        Assert.Equal(0, MemoryReport.EngineHeldFree(engine));
    }

    [Fact]
    public void WhatTheEngineHoldsFree_IsItsSecondFigure()
    {
        Assert.Equal(44 * Mib, MemoryReport.EngineHeldFree($"{35 * Mib} {44 * Mib} {5 * Mib} {94 * Mib} 41 7"));
    }
}
