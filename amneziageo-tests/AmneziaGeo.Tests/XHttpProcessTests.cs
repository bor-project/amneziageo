using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Checks ownership and startup of XHTTP children.
/// </summary>
public sealed class XHttpProcessTests
{
    [Fact]
    public async Task ACancelledStart_DoesNotLaunchARuntime()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XHttpProcess.StartAsync(Selection(), IPAddress.Loopback, stop.Token));
    }

    [Fact]
    public async Task AnUnknownTransport_IsRejectedBeforeLaunchingARuntime()
    {
        var selected = Selection();
        selected = selected with { Record = selected.Record with { Proxy = selected.Record.Proxy with { Transport = "unsupported" } } };

        var fault = await Assert.ThrowsAsync<TransportProxyFault>(() => XHttpProcess.StartAsync(selected, IPAddress.Loopback,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), CancellationToken.None));

        Assert.Equal("bad-proxy", fault.Code);
    }

    [Fact]
    public async Task AMissingRuntime_IsAnExplicitFault()
    {
        var fault = await Assert.ThrowsAsync<TransportProxyFault>(() => XHttpProcess.StartAsync(Selection(), IPAddress.Loopback,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), CancellationToken.None));

        Assert.Equal("xhttp-runtime-missing", fault.Code);
    }

    [WindowsFact("the system executable rejects the HTTP GET version request")]
    public async Task ADifferentExecutable_CannotReplaceThePinnedRuntime()
    {
        var other = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");

        var fault = await Assert.ThrowsAsync<TransportProxyFault>(() => XHttpProcess.CheckVersionAsync(other, CancellationToken.None));

        Assert.Equal("xhttp-runtime-version", fault.Code);
    }

    [XHttpRuntimeFact]
    public async Task Disposal_StopsOnlyTheOwnedChildAndReleasesItsPort()
    {
        await using var first = await XHttpProcess.StartAsync(Selection(), IPAddress.Loopback, CancellationToken.None);
        await using var second = await XHttpProcess.StartAsync(Selection(), IPAddress.Loopback, CancellationToken.None);
        Assert.True(first.IsRunning);
        Assert.True(second.IsRunning);
        Assert.NotEqual(first.ProcessId, second.ProcessId);
        Assert.NotEqual(first.LocalPort, second.LocalPort);
        using (var held = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            Assert.Throws<SocketException>(() => held.Bind(new IPEndPoint(IPAddress.Loopback, first.LocalPort)));
        }

        await first.DisposeAsync();
        await first.DisposeAsync();

        Assert.False(first.IsRunning);
        Assert.True(second.IsRunning);
        Assert.True(first.Completion.IsCompletedSuccessfully);
        using var reopened = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reopened.Bind(new IPEndPoint(IPAddress.Loopback, first.LocalPort));
    }

    [XHttpRuntimeFact]
    public async Task AnUnexpectedExit_IsReportedWithoutCredentials()
    {
        var notes = new List<string>();
        await using var carrier = await XHttpProcess.StartAsync(Selection(), IPAddress.Loopback, CancellationToken.None,
            (message, _) => notes.Add(message));
        using var child = Process.GetProcessById(carrier.ProcessId);

        child.Kill();
        await carrier.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(carrier.IsRunning);
        Assert.Contains(notes, line => line.Contains("exit code", StringComparison.Ordinal));
        Assert.DoesNotContain(notes, line => line.Contains(Selection().Record.Proxy.Auth.Credential, StringComparison.Ordinal));
    }

    private static ResolvedTransportProxy Selection()
    {
        var proxy = new OfferedProxy("front", "Test", "xhttp", "cdn.example.com", 443, "transport",
            new OfferedProxyTls("cdn.example.com", ""), new OfferedProxyAuth("vless", "ab8e048e-fc1c-4d43-8f5b-82d3d28ea9c3"),
            DateTimeOffset.MinValue, new OfferedXHttp("packet-up", "GET", "header"));
        return new ResolvedTransportProxy(new CatalogTransportProxy(new ProxyReference(Guid.NewGuid().ToString("D"), "front"), proxy),
            "test", "vpn.example.com", 51820);
    }
}

/// <summary>
/// Selects tests with a bundled HTTP GET runtime.
/// </summary>
public sealed class XHttpRuntimeFactAttribute : FactAttribute
{
    /// <summary>
    /// ctor
    /// </summary>
    public XHttpRuntimeFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!File.Exists(XHttpProcess.ExecutablePath))
        {
            Skip = "needs the HTTP GET runtime from build-http-get.ps1";
        }
    }
}
