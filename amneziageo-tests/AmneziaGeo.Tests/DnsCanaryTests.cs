using System.Net;

using AmneziaGeo.Decl;
using AmneziaGeo.Windows.App;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A browser asks one agreed name before it turns its own encrypted lookups on, and keeps asking the system when it
/// hears that the name does not exist: the resolver of the client answers so, and the rules by name keep seeing the
/// names the browser opens.
/// </summary>
public sealed class DnsCanaryTests
{
    private const int TypeA = 1;
    private const int TypeAaaa = 28;
    private const int NameDoesNotExist = 3;

    // An address of a range kept for documents: nothing answers there.
    private static readonly IPAddress _nowhere = IPAddress.Parse("192.0.2.53");

    [Theory]
    [InlineData("use-application-dns.net", true)]
    [InlineData("use-application-dns.net.", true)]
    [InlineData("USE-Application-DNS.net", true)]
    [InlineData("www.use-application-dns.net", false)]
    [InlineData("application-dns.net", false)]
    [InlineData("example.org", false)]
    public void TheNameABrowserAsksFirst_IsToldFromEveryOther(string name, bool canary)
    {
        Assert.Equal(canary, DnsProxy.IsCanary(name));
    }

    [Theory]
    [InlineData(TypeA)]
    [InlineData(TypeAaaa)]
    public async Task TheNameABrowserAsksFirst_IsSaidNotToExist(int type)
    {
        var proxy = new DnsProxy([new GeoDomain(GeoDomainKind.Domain, "example.org")], [], _nowhere, _nowhere, null, [], false, [], [], null, NullLogger<DnsProxy>.Instance, stripV6: false, listen: false);
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var answer = await Asked(proxy, DnsMessage.BuildQuery("use-application-dns.net", type), patience.Token);

        Assert.NotNull(answer);
        Assert.Equal(NameDoesNotExist, answer[3] & 0x0F);
    }

    // The answer of the proxy, null when it gave none while the patience lasted.
    private static async Task<byte[]?> Asked(DnsProxy proxy, byte[] query, CancellationToken patience)
    {
        try
        {
            return await proxy.AnswerAsync(query, patience);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
