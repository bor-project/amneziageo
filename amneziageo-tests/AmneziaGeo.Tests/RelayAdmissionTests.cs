using System.Text;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The relay of the Android tunnel listens on a loopback port any process of the device reaches, and only the engine
/// of the application hands streams over to it: a proxy request, a stream from another application and one from an
/// owner the system does not name are closed unanswered.
/// </summary>
public sealed class RelayAdmissionTests
{
    private const int Self = 10123;

    [Fact]
    public void FromSelf_TakesAConnectionOfThisApplication()
    {
        Assert.True(RelayAdmission.FromSelf(Self, Self));
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(10200)]
    [InlineData(-1)]
    public void FromSelf_RefusesAnotherOwnerAndAnUnknownOne(int owner)
    {
        Assert.False(RelayAdmission.FromSelf(owner, Self));
    }

    [Fact]
    public void HandsOver_TakesTheHeaderTheEngineWrites()
    {
        Assert.True(RelayAdmission.HandsOver(Header()));
    }

    [Theory]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n")]
    public void HandsOver_RefusesAProxyRequest(string request)
    {
        Assert.False(RelayAdmission.HandsOver(Encoding.ASCII.GetBytes(request)));
    }

    [Fact]
    public void HandsOver_RefusesTheMarkWithoutTheRestOfTheHeader()
    {
        Assert.False(RelayAdmission.HandsOver("AGST"u8));
    }

    // The header the engine writes: the mark, then the source and the destination the stream was opened for.
    private static byte[] Header()
    {
        var head = new byte[RelayAdmission.HeadLength];
        "AGST"u8.CopyTo(head);
        byte[] pair = [10, 8, 1, 2, 0xC3, 0x50, 93, 184, 215, 14, 0x01, 0xBB];
        pair.CopyTo(head, 4);
        return head;
    }
}
