using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A tunnel service that ends by itself leaves a code with the service manager, and the journal names it in words.
/// </summary>
public sealed class ServiceExitTests
{
    [Fact]
    public void ACleanStop_HasNoWords()
    {
        Assert.Equal(string.Empty, ServiceExit.Describe(0, 0));
    }

    [Fact]
    public void ACodeOfTheSystem_IsNamedByItsNumber()
    {
        Assert.StartsWith("error 87 (", ServiceExit.Describe(87, 0), StringComparison.Ordinal);
        Assert.StartsWith("error 1066 (", ServiceExit.Describe(1066, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeOfTheEngine_IsNamedInTheWordsOfTheEngine()
    {
        Assert.Equal("engine error 3 (Unable to create Wintun interface)", ServiceExit.Describe(1066, 3));
        Assert.Equal("engine error 9 (Unable to set interface addresses, routes, dns, and/or interface settings)", ServiceExit.Describe(1066, 9));
    }

    [Fact]
    public void ACodeTheEngineDoesNotName_KeepsItsNumber()
    {
        Assert.Equal("engine error 40", ServiceExit.Describe(1066, 40));
    }
}
