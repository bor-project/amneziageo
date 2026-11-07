using System.Text.Json;
using AmneziaGeo.Cli;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;
using Commands = AmneziaGeo.Cli.TransportProxyCommands;

namespace AmneziaGeo.Tests;

/// <summary>
/// Checks transport proxy console operations.
/// </summary>
[Collection("Output")]
public sealed class TransportProxyCliTests : IDisposable
{
    private const string Source = "bcb4b9ad-00bc-4c2f-b32b-4655a1655a02";
    private static readonly ProxyReference Reference = new(Source, "front");
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public TransportProxyCliTests()
    {
        Output.Sink = _console;
        Output.Json = false;
        Output.Quiet = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Output.Sink = new SystemConsoleSink();
        Output.Json = false;
        TextInput.StandardInput = null;
    }

    [Fact]
    public async Task List_PrintsPublicMetadataWithoutCredentials()
    {
        Output.Json = true;
        var link = new Link((_, _) => new IpcAck(true,
            """[{"reference":{"source":"bcb4b9ad-00bc-4c2f-b32b-4655a1655a02","id":"front"},"name":"CDN","transport":"xhttp","host":"cdn.example.com","port":443,"path":"tunnel","local":false,"sources":["server-a"],"auth":{"credential":"never-print-this"}}]"""));

        Assert.Equal(Exit.Ok, await Commands.RunAsync(link, ["list"]));

        Assert.Equal(IpcContract.OpListTransportProxies, Assert.Single(link.Calls).Op);
        var text = _console.ToString();
        Assert.Contains("cdn.example.com", text, StringComparison.Ordinal);
        Assert.Contains("server-a", text, StringComparison.Ordinal);
        Assert.DoesNotContain("never-print-this", text, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Selection_SendsNoneOrTheExactRegisteredReference()
    {
        var link = new Link();

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["proxy", "server-b", "none"]));
        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["proxy", "server-b", Source.ToUpperInvariant() + "/front"]));

        Assert.Equal("null", link.Calls[0].Args[1]);
        Assert.Equal(IpcContract.OpSelectTransportProxy, link.Calls[0].Op);
        Assert.Equal("server-b", link.Calls[1].Args[0]);
        Assert.Equal(Reference, JsonSerializer.Deserialize<ProxyReference>(link.Calls[1].Args[1], IpcJson.Options));
    }

    [Theory]
    [InlineData("front")]
    [InlineData("not-a-source/front")]
    [InlineData(Source + "/front/extra")]
    [InlineData(Source + "/")]
    public async Task InvalidReference_DoesNotReachTheAgent(string reference)
    {
        var link = new Link();

        Assert.Equal(Exit.Usage, await ConfigCommands.RunAsync(link, ["proxy", "server-b", reference]));
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task Save_ReadsTheDefinitionFromStdinAndPrintsOnlyItsReference()
    {
        var draft = Definition();
        TextInput.StandardInput = new StringReader(JsonSerializer.Serialize(draft, IpcJson.Options));
        var link = new Link((_, _) => new IpcAck(true, JsonSerializer.Serialize(Reference, IpcJson.Options)));

        Assert.Equal(Exit.Ok, await Commands.RunAsync(link, ["save", "--stdin"]));

        var call = Assert.Single(link.Calls);
        Assert.Equal(IpcContract.OpSaveTransportProxy, call.Op);
        Assert.Equal("", call.Args[0]);
        Assert.Equal(draft, JsonSerializer.Deserialize<OfferedProxy>(call.Args[1], IpcJson.Options));
        Assert.Contains(Source + "/front", _console.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(draft.Auth.Credential, _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowLocal_PrintsTheExplicitlyRequestedCredential()
    {
        var local = new CatalogTransportProxy(Reference, Definition(), Local: true);
        var link = new Link((_, _) => new IpcAck(true, JsonSerializer.Serialize(local, IpcJson.Options)));

        Assert.Equal(Exit.Ok, await Commands.RunAsync(link, ["showlocal", Source + "/front"]));

        Assert.Equal(IpcContract.OpGetTransportProxy, Assert.Single(link.Calls).Op);
        Assert.Contains(local.Proxy.Auth.Credential, _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_DoesNotChangeTransportOrChooseAnotherProxy()
    {
        var link = new Link((_, _) => new IpcAck(false, "proxy-missing"));

        Assert.Equal(Exit.Failed, await ConfigCommands.RunAsync(link, ["proxy", "server-b", Source + "/front"]));

        Assert.Equal(IpcContract.OpSelectTransportProxy, Assert.Single(link.Calls).Op);
        Assert.Contains("proxy-missing", _console.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyWebsocketToggle_IsExplicitWhileMtuPreservesTheProxyChoice()
    {
        var link = new Link(configs: [new ConfigEntry("office", "vpn.example:51820", false, "idle", [],
            TransportProxy: Reference, TransportProxyChosen: true)]);

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["websocket", "office", "off"]));
        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["mtu", "office", "1380"]));

        Assert.Equal("legacy", link.Calls[0].Args[11]);
        Assert.DoesNotContain("legacy", link.Calls[1].Args);
    }

    [Fact]
    public async Task ConfigList_ShowsTheProxyChoiceInsteadOfTheLegacySwitch()
    {
        var link = new Link(configs:
        [
            new ConfigEntry("proxied", "vpn.example:51820", false, "idle", [], TransportProxy: Reference, TransportProxyChosen: true),
            new ConfigEntry("direct", "vpn.example:51820", false, "idle", [], WebSocket: true, TransportProxyChosen: true),
        ]);

        Assert.Equal(Exit.Ok, await ConfigCommands.RunAsync(link, ["list"]));

        var text = _console.ToString();
        Assert.Contains("PROXY", text, StringComparison.Ordinal);
        Assert.Contains(Source + "/front", text, StringComparison.Ordinal);
        Assert.Contains("none", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no front", text, StringComparison.Ordinal);
    }

    private static OfferedProxy Definition() => new("front", "CDN", "xhttp", "cdn.example.com", 443, "tunnel",
        new OfferedProxyTls("cdn.example.com", ""), new OfferedProxyAuth("vless", "ab8e048e-fc1c-4d43-8f5b-82d3d28ea9c3"),
        DateTimeOffset.MinValue, new OfferedXHttp("packet-up", "GET", "header"));

    private sealed class Link : IAgentLink
    {
        private readonly Func<string, string[], IpcAck> _reply;
        public List<(string Op, string[] Args)> Calls { get; } = [];

        /// <summary>
        /// ctor
        /// </summary>
        public Link(Func<string, string[], IpcAck>? reply = null, IReadOnlyList<ConfigEntry>? configs = null)
        {
            _reply = reply ?? ((_, _) => new IpcAck(true, ""));
            Snapshot = new StatusSnapshot("test", null, configs ?? []);
        }

        /// <inheritdoc/>
        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public StatusSnapshot Snapshot { get; }

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args)
        {
            Calls.Add((op, args));
            return Task.FromResult(_reply(op, args));
        }
    }
}
