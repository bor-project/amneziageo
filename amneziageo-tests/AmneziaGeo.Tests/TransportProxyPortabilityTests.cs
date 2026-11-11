using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class TransportProxyPortabilityTests : IAsyncLifetime
{
    private const string Source = "38b49ed1-e8cd-496d-b4cd-a2512b5f578d";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _sourcePath = Path.Combine(Path.GetTempPath(), $"ageo-proxy-portable-source-{Guid.NewGuid():N}.db");
    private readonly string _targetPath = Path.Combine(Path.GetTempPath(), $"ageo-proxy-portable-target-{Guid.NewGuid():N}.db");
    private SqliteStateStore _source = null!;
    private SqliteStateStore _target = null!;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _source = new SqliteStateStore(_sourcePath);
        _target = new SqliteStateStore(_targetPath);
        await _source.InitializeAsync().ConfigureAwait(false);
        await _target.InitializeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _source.ClearPool();
        _target.ClearPool();
        foreach (var path in new[] { _sourcePath, _targetPath })
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OlderBundlesWithoutASelectionKeepTheExistingChoice(int version)
    {
        await ConfigAsync(_target, "kept", Key("server")).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_target);
        var reference = await catalog.SaveLocalAsync(null, Proxy("local", "local-secret")).ConfigureAwait(false);
        await catalog.SelectAsync("kept", reference).ConfigureAwait(false);
        var before = (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();
        var wire = PortableBundle.Serialize(new PortableBundle.Bundle(PortableBundle.FormatTag, version,
            [new PortableBundle.ConfigBlock("kept", "old-config", null, null)], []));
        var block = Assert.Single(PortableBundle.Deserialize(wire)!.Configs);

        await block.ApplyTransportProxyAsync(_target, "kept", default).ConfigureAwait(false);

        Assert.Null(block.TransportProxy);
        Assert.Equal(before, (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
        Assert.Equal(reference, (await catalog.SelectionAsync("kept").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task AbsentSelectionsDoNotRunLegacyMigrationDuringImport()
    {
        await ConfigAsync(_target, "old", Key("server")).ConfigureAwait(false);
        await _target.SetConfigTransportAsync(new ConfigTransport("old", true,
            WebSocketHost: "wss://relay.example.net:9443/legacy")).ConfigureAwait(false);
        var block = new PortableBundle.ConfigBlock("old", "old-config", null, null);

        await block.ApplyTransportProxyAsync(_target, "old", default).ConfigureAwait(false);

        var held = await _target.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.Equal(0, held.Revision);
        Assert.Empty(held.Selections);
        Assert.True((await _target.GetConfigTransportAsync("old").ConfigureAwait(false))!.UseWebSocket);
    }

    [Fact]
    public async Task ExplicitNoneSurvivesSerializationAndOverridesThePreviousChoice()
    {
        await ConfigAsync(_source, "direct", Key("source-server")).ConfigureAwait(false);
        await TransportProxyCatalog.For(_source).SelectAsync("direct", null).ConfigureAwait(false);
        var exported = await BlockAsync(_source, "direct").ConfigureAwait(false);
        var wire = PortableBundle.Serialize(new PortableBundle.Bundle(PortableBundle.FormatTag, PortableBundle.CurrentVersion, [exported], []));
        var imported = Assert.Single(PortableBundle.Deserialize(wire)!.Configs);
        Assert.Equal(3, PortableBundle.CurrentVersion);
        Assert.NotNull(imported.TransportProxy);
        Assert.Null(imported.TransportProxy.Reference);
        await ConfigAsync(_target, "replaced", Key("target-server")).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_target);
        var previous = await catalog.SaveLocalAsync(null, Proxy("local")).ConfigureAwait(false);
        await catalog.SelectAsync("replaced", previous).ConfigureAwait(false);
        await _target.SetConfigTransportAsync(new ConfigTransport("replaced", true)).ConfigureAwait(false);

        await imported.ApplyTransportProxyAsync(_target, "replaced", default).ConfigureAwait(false);

        Assert.Null((await catalog.SelectionAsync("replaced").ConfigureAwait(false)).Reference);
        Assert.Null(await catalog.ResolveAsync("replaced").ConfigureAwait(false));
        Assert.True((await _target.GetConfigTransportAsync("replaced").ConfigureAwait(false))!.UseWebSocket);
        Assert.Single((await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals);
    }

    [Fact]
    public async Task LocalDefinitionsMoveToTheReceivingNamespaceAndDeduplicateAcrossConfigurations()
    {
        await ConfigAsync(_source, "one", Key("server")).ConfigureAwait(false);
        await ConfigAsync(_source, "two", Key("server")).ConfigureAwait(false);
        var sourceCatalog = TransportProxyCatalog.For(_source);
        var original = await sourceCatalog.SaveLocalAsync(null, Proxy("local", "portable-local-secret")).ConfigureAwait(false);
        await sourceCatalog.SelectAsync("one", original).ConfigureAwait(false);
        await sourceCatalog.SelectAsync("two", original).ConfigureAwait(false);
        var first = await BlockAsync(_source, "one").ConfigureAwait(false);
        var second = await BlockAsync(_source, "two").ConfigureAwait(false);
        await ConfigAsync(_target, "imported-one", Key("target-one")).ConfigureAwait(false);
        await ConfigAsync(_target, "imported-two", Key("target-two")).ConfigureAwait(false);

        await first.ApplyTransportProxyAsync(_target, "imported-one", default).ConfigureAwait(false);
        await second.ApplyTransportProxyAsync(_target, "imported-two", default).ConfigureAwait(false);

        var state = await _target.GetTransportProxyStateAsync().ConfigureAwait(false);
        var local = Assert.Single(state.Locals);
        Assert.NotEqual(original.Source, local.Reference.Source);
        Assert.NotEqual(original.Id, local.Reference.Id);
        Assert.Equal(state.LocalSource, local.Reference.Source);
        Assert.All(state.Selections, one => Assert.Equal(local.Reference, one.Reference));
        Assert.Equal("portable-local-secret", local.Proxy.Auth.Credential);
        var revision = state.Revision;
        await first.ApplyTransportProxyAsync(_target, "imported-one", default).ConfigureAwait(false);
        Assert.Equal(revision, (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision);
    }

    [Fact]
    public async Task MatchingIncomingIdsNeverOverwriteAnExistingLocalDefinition()
    {
        await ConfigAsync(_source, "one", Key("server")).ConfigureAwait(false);
        var sourceCatalog = TransportProxyCatalog.For(_source);
        var original = await sourceCatalog.SaveLocalAsync(null, Proxy("local", "incoming-secret")).ConfigureAwait(false);
        await sourceCatalog.SelectAsync("one", original).ConfigureAwait(false);
        var incoming = (await BlockAsync(_source, "one").ConfigureAwait(false)).TransportProxy!;
        await ConfigAsync(_target, "imported", Key("target")).ConfigureAwait(false);
        var targetCatalog = TransportProxyCatalog.For(_target);
        var existing = await targetCatalog.SaveLocalAsync(null, Proxy("local", "existing-secret")).ConfigureAwait(false);
        var colliding = incoming with
        {
            Reference = incoming.Reference! with { Id = existing.Id },
            Retired = incoming.Retired! with { Reference = incoming.Reference! with { Id = existing.Id } },
            Local = incoming.Local! with { Proxy = incoming.Local!.Proxy with { Id = existing.Id } },
        };

        await TransportProxyPortability.ApplyAsync(_target, "imported", colliding).ConfigureAwait(false);

        var state = await _target.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.Equal(2, state.Locals.Count);
        Assert.Equal("existing-secret", state.Locals.Single(one => one.Reference == existing).Proxy.Auth.Credential);
        var selection = Assert.Single(state.Selections).Reference;
        Assert.NotEqual(existing, selection);
        Assert.NotEqual(existing.Id, selection!.Id);
        Assert.Equal("incoming-secret", (await targetCatalog.ResolveAsync("imported").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
    }

    [Fact]
    public async Task ConcurrentLocalImportsShareOneDefinitionWithoutLosingEitherChoice()
    {
        await ConfigAsync(_source, "one", Key("server")).ConfigureAwait(false);
        var sourceCatalog = TransportProxyCatalog.For(_source);
        var reference = await sourceCatalog.SaveLocalAsync(null, Proxy("local")).ConfigureAwait(false);
        await sourceCatalog.SelectAsync("one", reference).ConfigureAwait(false);
        var block = (await BlockAsync(_source, "one").ConfigureAwait(false)).TransportProxy!;
        await ConfigAsync(_target, "a", Key("target-a")).ConfigureAwait(false);
        await ConfigAsync(_target, "b", Key("target-b")).ConfigureAwait(false);
        var other = new SqliteStateStore(_targetPath);

        await Task.WhenAll(Task.Run(() => TransportProxyPortability.ApplyAsync(_target, "a", block)),
            Task.Run(() => TransportProxyPortability.ApplyAsync(other, "b", block))).ConfigureAwait(false);

        var state = await _target.GetTransportProxyStateAsync().ConfigureAwait(false);
        var local = Assert.Single(state.Locals);
        Assert.Equal(2, state.Selections.Count);
        Assert.All(state.Selections, one => Assert.Equal(local.Reference, one.Reference));
    }

    [Fact]
    public async Task ServerExportsCarryOnlyAReferencePublicMetadataAndThePinnedIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync(_source, "publisher", Key("server")).ConfigureAwait(false);
        await ConfigAsync(_source, "consumer", Key("consumer-server")).ConfigureAwait(false);
        var sourceCatalog = TransportProxyCatalog.For(_source);
        await sourceCatalog.SynchronizeAsync("publisher", publisher, SignedOffer(key, publisher.ServerKey, "server-secret-never-exported")).ConfigureAwait(false);
        var reference = new ProxyReference(ProxySourceIdentity.Signed(Source), "one");
        await sourceCatalog.SelectAsync("consumer", reference).ConfigureAwait(false);

        var exported = await BlockAsync(_source, "consumer").ConfigureAwait(false);
        var wire = PortableBundle.Serialize(new PortableBundle.Bundle(PortableBundle.FormatTag, PortableBundle.CurrentVersion, [exported], []));

        Assert.NotNull(exported.TransportProxy);
        Assert.Null(exported.TransportProxy.Local);
        Assert.Equal(reference, exported.TransportProxy.Reference);
        Assert.Equal("cdn.example.net", exported.TransportProxy.Retired!.Host);
        Assert.Equal(Source, exported.TransportProxy.Trust!.AdvertisedSource);
        Assert.Equal(reference.Source, exported.TransportProxy.Trust.Source);
        Assert.Equal(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), exported.TransportProxy.Trust.PublicKey);
        Assert.DoesNotContain("server-secret-never-exported", wire);
        Assert.DoesNotContain("credential", wire, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnImportedServerReferenceStaysMissingUntilAuthenticatedHelloSuppliesItsCredential()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync(_source, "publisher", Key("server")).ConfigureAwait(false);
        var sourceCatalog = TransportProxyCatalog.For(_source);
        await sourceCatalog.SynchronizeAsync("publisher", publisher, SignedOffer(key, publisher.ServerKey, "old-secret")).ConfigureAwait(false);
        var reference = new ProxyReference(ProxySourceIdentity.Signed(Source), "one");
        await sourceCatalog.SelectAsync("publisher", reference).ConfigureAwait(false);
        var exported = await BlockAsync(_source, "publisher").ConfigureAwait(false);
        await ConfigAsync(_target, "consumer", Key("target-server")).ConfigureAwait(false);
        var targetCatalog = TransportProxyCatalog.For(_target);

        await exported.ApplyTransportProxyAsync(_target, "consumer", default).ConfigureAwait(false);

        Assert.Equal(reference, (await targetCatalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
        await FaultAsync("proxy-missing", () => targetCatalog.ResolveAsync("consumer")).ConfigureAwait(false);
        Assert.True(Assert.Single(await targetCatalog.ListAsync().ConfigureAwait(false)).Missing);
        Assert.Empty((await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).Publications);
        var importedPublisher = await ConfigAsync(_target, "publisher", publisher.ServerKey).ConfigureAwait(false);
        await targetCatalog.SynchronizeAsync("publisher", importedPublisher,
            SignedOffer(key, importedPublisher.ServerKey, "fresh-secret-from-hello")).ConfigureAwait(false);

        Assert.Equal("fresh-secret-from-hello", (await targetCatalog.ResolveAsync("consumer").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
    }

    [Fact]
    public async Task AConflictingImportedPinLeavesTheWholeAggregateUnchanged()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync(_target, "publisher", Key("server")).ConfigureAwait(false);
        await ConfigAsync(_target, "consumer", Key("target-server")).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_target);
        await catalog.SynchronizeAsync("publisher", publisher, SignedOffer(trusted, publisher.ServerKey, "retained")).ConfigureAwait(false);
        var local = await catalog.SaveLocalAsync(null, Proxy("local", "retained-local")).ConfigureAwait(false);
        await catalog.SelectAsync("consumer", local).ConfigureAwait(false);
        var reference = new ProxyReference(ProxySourceIdentity.Signed(Source), "one");
        var block = new ProxySelectionBlock(reference, Public(reference),
            new TransportProxyTrust(reference.Source, "signed", Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), AdvertisedSource: Source));
        var before = (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();

        var fault = await Assert.ThrowsAsync<TransportProxyFault>(() => TransportProxyPortability.ApplyAsync(_target, "consumer", block)).ConfigureAwait(false);

        Assert.Equal("proxy-source-conflict", fault.Code);
        Assert.Equal(before, (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
    }

    [Fact]
    public async Task AServerReferenceCannotAliasTheReceivingLocalNamespace()
    {
        await ConfigAsync(_target, "consumer", Key("server")).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_target);
        var local = await catalog.SaveLocalAsync(null, Proxy("local")).ConfigureAwait(false);
        await catalog.SelectAsync("consumer", null).ConfigureAwait(false);
        var block = new ProxySelectionBlock(local);
        var before = (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();

        var error = await Assert.ThrowsAsync<TransportProxyFault>(() => TransportProxyPortability.ApplyAsync(_target, "consumer", block)).ConfigureAwait(false);

        Assert.Equal("proxy-source-conflict", error.Code);
        Assert.Equal(before, (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
    }

    [Fact]
    public async Task LegacyLocalImportsPreserveRestrictionsAndKeepIndependentConfigurationOwners()
    {
        var issuer = Key("server");
        await ConfigAsync(_source, "old", issuer).ConfigureAwait(false);
        await _source.SetConfigTransportAsync(new ConfigTransport("old", true,
            WebSocketHost: "wss://account:password@relay.example.net:9443/legacy")).ConfigureAwait(false);
        var exported = await BlockAsync(_source, "old").ConfigureAwait(false);
        Assert.True(exported.TransportProxy!.Local!.Legacy);
        Assert.Equal(issuer, exported.TransportProxy.Local.LegacyIssuer);
        Assert.Equal(51820, exported.TransportProxy.Local.LegacyPort);
        Assert.False(exported.TransportProxy.Local.LegacyOffered);
        await ConfigAsync(_target, "first", issuer).ConfigureAwait(false);
        await ConfigAsync(_target, "second", issuer).ConfigureAwait(false);
        await ConfigAsync(_target, "other", Key("other-server")).ConfigureAwait(false);

        await exported.ApplyTransportProxyAsync(_target, "first", default).ConfigureAwait(false);
        await exported.ApplyTransportProxyAsync(_target, "second", default).ConfigureAwait(false);

        var catalog = TransportProxyCatalog.For(_target);
        var first = await catalog.ResolveAsync("first").ConfigureAwait(false);
        var second = await catalog.ResolveAsync("second").ConfigureAwait(false);
        Assert.NotEqual(first!.Record.Reference, second!.Record.Reference);
        Assert.Equal("first", first.Record.LegacyPublisher);
        Assert.Equal("second", second.Record.LegacyPublisher);
        Assert.Equal("account:password", second.Record.Proxy.Auth.Credential);
        var denied = await Assert.ThrowsAsync<TransportProxyFault>(() => catalog.SelectAsync("other", second.Record.Reference)).ConfigureAwait(false);
        Assert.Equal("proxy-legacy-target", denied.Code);
        Assert.Null((await catalog.SelectionAsync("other").ConfigureAwait(false)).Reference);
        await catalog.ForgetAsync("first").ConfigureAwait(false);
        Assert.NotNull(await catalog.ResolveAsync("second").ConfigureAwait(false));
    }

    [Fact]
    public async Task ADeletedLocalChoiceRemainsMissingInANewLocalNamespace()
    {
        await ConfigAsync(_source, "old", Key("server")).ConfigureAwait(false);
        var sourceCatalog = TransportProxyCatalog.For(_source);
        var original = await sourceCatalog.SaveLocalAsync(null, Proxy("local", "deleted-secret")).ConfigureAwait(false);
        await sourceCatalog.SelectAsync("old", original).ConfigureAwait(false);
        await sourceCatalog.RemoveLocalAsync(original).ConfigureAwait(false);
        var exported = await BlockAsync(_source, "old").ConfigureAwait(false);
        Assert.Null(exported.TransportProxy!.Local);
        Assert.True(exported.TransportProxy.Retired!.Local);
        await ConfigAsync(_target, "imported", Key("target")).ConfigureAwait(false);

        await exported.ApplyTransportProxyAsync(_target, "imported", default).ConfigureAwait(false);

        var state = await _target.GetTransportProxyStateAsync().ConfigureAwait(false);
        var reference = Assert.Single(state.Selections).Reference;
        Assert.NotNull(reference);
        Assert.Equal(state.LocalSource, reference.Source);
        Assert.NotEqual(original, reference);
        Assert.Empty(state.Locals);
        await FaultAsync("proxy-missing", () => TransportProxyCatalog.For(_target).ResolveAsync("imported")).ConfigureAwait(false);
        var revision = (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision;
        await exported.ApplyTransportProxyAsync(_target, "imported", default).ConfigureAwait(false);
        Assert.Equal(revision, (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision);
    }

    [Fact]
    public async Task InvalidBlocksAreRejectedBeforeChangingTheCatalog()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        await ConfigAsync(_target, "consumer", Key("server")).ConfigureAwait(false);
        var catalog = TransportProxyCatalog.For(_target);
        await catalog.SelectAsync("consumer", null).ConfigureAwait(false);
        var reference = new ProxyReference(ProxySourceIdentity.Signed(Source), "one");
        var trust = new TransportProxyTrust(reference.Source, "signed", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), AdvertisedSource: Source);
        var local = new LocalTransportProxyBlock(Proxy("one", "local-secret"));
        var blocks = new[]
        {
            new ProxySelectionBlock(null, Local: local),
            new ProxySelectionBlock(null, Trust: trust),
            new ProxySelectionBlock(new ProxyReference("invalid", "one")),
            new ProxySelectionBlock(reference with { Id = "../one" }),
            new ProxySelectionBlock(reference, Local: local with { Proxy = local.Proxy with { Transport = "unknown" } }),
            new ProxySelectionBlock(reference, Trust: trust with { PublicKey = "not-base64" }),
            new ProxySelectionBlock(reference, Trust: trust with { PublicKey = Convert.ToBase64String(otherCurve.ExportSubjectPublicKeyInfo()) }),
            new ProxySelectionBlock(reference, Trust: trust with { PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo().Append((byte)0).ToArray()) }),
            new ProxySelectionBlock(new ProxyReference(Source, "one"), Trust: trust with { Source = Source }),
            new ProxySelectionBlock(reference, Public(reference with { Id = "different" })),
        };
        var before = (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload();

        foreach (var block in blocks)
        {
            var error = await Assert.ThrowsAsync<TransportProxyFault>(() => TransportProxyPortability.ApplyAsync(_target, "consumer", block)).ConfigureAwait(false);
            Assert.Equal("bad-proxy", error.Code);
            Assert.Equal(before, (await _target.GetTransportProxyStateAsync().ConfigureAwait(false)).ToPayload());
        }
    }

    [Fact]
    public async Task AValidUnsignedPinPreservesItsEffectiveIssuerNamespace()
    {
        var issuer = Key("server");
        var reference = new ProxyReference(ProxySourceIdentity.Unsigned(Source, issuer), "one");
        var block = new ProxySelectionBlock(reference, Public(reference),
            new TransportProxyTrust(reference.Source, "unsigned", Issuer: issuer, AdvertisedSource: Source));
        await ConfigAsync(_target, "consumer", Key("target-server")).ConfigureAwait(false);

        await TransportProxyPortability.ApplyAsync(_target, "consumer", block).ConfigureAwait(false);

        var held = await _target.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.Equal(reference, Assert.Single(held.Selections).Reference);
        Assert.Equal(block.Trust, Assert.Single(held.Trust));
        await FaultAsync("proxy-missing", () => TransportProxyCatalog.For(_target).ResolveAsync("consumer")).ConfigureAwait(false);
    }

    private static RetiredTransportProxy Public(ProxyReference reference) => new(reference, "Proxy one", "websocket", "cdn.example.net", 443, "transport", false);

    private static OfferedProxy Proxy(string id, string credential = "local-credential") => new(id, "Local proxy", "websocket",
        "relay.example.net", 9443, "relay", new OfferedProxyTls("relay.example.net", ""), new OfferedProxyAuth("bearer", credential),
        new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

    private static string Key(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<ServiceTarget> ConfigAsync(IStateStore store, string name, string issuer)
    {
        var text = $"""
                    [Interface]
                    PrivateKey = {Key("client-" + name)}
                    Address = 10.8.0.2/32

                    [Peer]
                    PublicKey = {issuer}
                    Endpoint = vpn.example.net:51820
                    AllowedIPs = 0.0.0.0/0
                    """;
        await store.SaveConfigAsync(name, text).ConfigureAwait(false);
        return ConfigServices.Target(text)!;
    }

    private static async Task<PortableBundle.ConfigBlock> BlockAsync(IStateStore store, string name) =>
        await new PortableBundle.ConfigBlock(name, (await store.GetConfigTextAsync(name).ConfigureAwait(false))!, null, null)
            .WithTransportProxyAsync(store, name, default).ConfigureAwait(false);

    private static async Task FaultAsync(string code, Func<Task<ResolvedTransportProxy?>> action)
    {
        var error = await Assert.ThrowsAsync<TransportProxyFault>(action).ConfigureAwait(false);
        Assert.Equal(code, error.Code);
    }

    private static ServerOffer SignedOffer(ECDsa key, string issuer, string credential)
    {
        var message = Encoding.UTF8.GetBytes("amneziageo.proxy-source.v1\n" + Source + "\n" + issuer);
        var proof = new OfferedProxySourceProof(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), issuer,
            Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var proxy = Proxy("one", credential) with { Name = "Proxy one", Host = "cdn.example.net", Port = 443,
            Path = "transport", Tls = new OfferedProxyTls("cdn.example.net", "") };
        return new ServerOffer("1", "phone", new Dictionary<string, JsonElement>
        {
            [ServerOffer.ProxiesFeature] = JsonSerializer.SerializeToElement(new OfferedProxyCatalog(1, Source, [proxy], proof), Web),
        });
    }
}
