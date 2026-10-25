using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What a failed request is read down to: the cause the network gave it, or nothing when the network is not to
/// blame and the stack says more.
/// </summary>
public sealed class NetworkFailureTests
{
    [Fact]
    public void AnAndroidResolverFailure_NamesTheHostAndTheCode()
    {
        var error = new HttpRequestException("Connection failure",
            new IOException("Unable to resolve host \"api.github.com\": No address associated with hostname",
                new InvalidOperationException("android_getaddrinfo failed: EAI_NODATA (No address associated with hostname)")));

        Assert.Equal("the name api.github.com did not resolve (EAI_NODATA)", NetworkFailure.Describe(error));
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, "the name did not resolve")]
    [InlineData(SocketError.ConnectionRefused, "the connection was refused")]
    [InlineData(SocketError.NetworkUnreachable, "no route to the server")]
    [InlineData(SocketError.TimedOut, "timed out")]
    public void ASocketFailure_ReadsByItsCode(SocketError code, string cause)
    {
        var error = new HttpRequestException("request failed", new SocketException((int)code));

        Assert.Equal(cause, NetworkFailure.Describe(error));
    }

    [Fact]
    public void AnAndroidTimeout_ReadsAsTimedOut()
    {
        var error = new HttpRequestException("Connection failure", new IOException("failed to connect to api.github.com: connect timed out"));

        Assert.Equal("timed out", NetworkFailure.Describe(error));
    }

    [Fact]
    public void AServerThatAnswered_IsReadByItsStatus()
    {
        var error = new HttpRequestException("Response status code does not indicate success: 403 (Forbidden).", null, HttpStatusCode.Forbidden);

        Assert.Equal("the server answered 403", NetworkFailure.Describe(error));
    }

    [Fact]
    public void AnUnknownConnectionFailure_KeepsItsFirstLine()
    {
        var error = new HttpRequestException("Connection failure", new IOException("stream was closed\nat the far end"));

        Assert.Equal("the connection failed: stream was closed", NetworkFailure.Describe(error));
    }

    [Fact]
    public void AnAndroidAbortedRead_ReadsAsAborted()
    {
        var error = new WebException("Software caused connection abort", new Java.Net.SocketException("Software caused connection abort"));

        Assert.Equal("the connection was aborted", NetworkFailure.Describe(error));
    }

    [Fact]
    public void AnAndroidFailureOfAnotherKind_KeepsItsFirstLine()
    {
        var error = new WebException("Connection closed by peer\nat the far end");

        Assert.Equal("the connection failed: Connection closed by peer", NetworkFailure.Describe(error));
    }

    [Fact]
    public void AJavaSocketFailureOnItsOwn_IsTheNetworks()
    {
        Assert.Equal("the connection failed: Broken pipe", NetworkFailure.Describe(new Java.Net.SocketException("Broken pipe")));
    }

    [Fact]
    public void ARowOfANetworkFailure_NamesWhatFailedTheCauseAndTheAddress()
    {
        var row = NetworkFailure.Row("'zkeenip' could not be checked", new WebException("Software caused connection abort"), "https://github.com/x");

        Assert.Equal("'zkeenip' could not be checked: the connection was aborted; https://github.com/x", row);
    }

    [Fact]
    public void ARowOfAFailureOutsideTheNetwork_IsNone()
    {
        Assert.Null(NetworkFailure.Row("the manifest could not be read", new JsonException("not json"), "https://github.com/x"));
    }

    [Fact]
    public void AnAddress_DropsTheUserTheQueryAndTheFragment()
    {
        var address = NetworkFailure.Address("https://user:secret@github.com/jameszeroX/zkeen-ip/releases/latest/download/zkeenip.dat?token=x#part");

        Assert.Equal("https://github.com/jameszeroX/zkeen-ip/releases/latest/download/zkeenip.dat", address);
    }

    [Fact]
    public void AnAddressWithASecretPath_KeepsTheHostAndThePort()
    {
        Assert.Equal("https://panel.example:8443", NetworkFailure.Address("https://panel.example:8443/sub/abcdef", path: false));
    }

    [Fact]
    public void NoAddress_IsEmpty()
    {
        Assert.Equal(string.Empty, NetworkFailure.Address("not an address"));
    }

    [Fact]
    public void AFailureOutsideTheNetwork_IsLeftToTheStack()
    {
        Assert.Null(NetworkFailure.Describe(new JsonException("the manifest is not json")));
    }
}
