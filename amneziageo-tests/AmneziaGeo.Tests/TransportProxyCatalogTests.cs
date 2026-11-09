using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed class TransportProxyCatalogTests : IAsyncLifetime
{
    private const string Source = "b4ca7082-c757-41cc-8094-1a3c0048375c";
    private static readonly DateTimeOffset Updated = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-proxy-catalog-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;
    private TransportProxyCatalog _catalog = null!;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
        _catalog = new TransportProxyCatalog(_store);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ManualLegacyMigrationPreservesTheChosenFrontAndCredentials()
    {
        var target = await ConfigAsync("manual", Key("server"), 51820).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("manual", true,
            WebSocketHost: "wss://account:password@relay.example.net:9443/old_path", WebSocketPort: 9443)).ConfigureAwait(false);

        var choice = await _catalog.SelectionAsync("manual").ConfigureAwait(false);
        var resolved = await _catalog.ResolveAsync("manual").ConfigureAwait(false);
        var revision = (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision;
        await _catalog.EnsureSelectionAsync("manual").ConfigureAwait(false);

        Assert.NotNull(choice.Reference);
        Assert.NotNull(resolved);
        Assert.True(resolved.Record.Local);
        Assert.True(resolved.Record.Legacy);
        Assert.False(resolved.Record.LegacyOffered);
        Assert.Equal("relay.example.net", resolved.Record.Proxy.Host);
        Assert.Equal(9443, resolved.Record.Proxy.Port);
        Assert.Equal("old_path", resolved.Record.Proxy.Path);
        Assert.Equal(new OfferedProxyAuth("legacy", "account:password"), resolved.Record.Proxy.Auth);
        Assert.Equal(target.Host, resolved.TargetHost);
        Assert.Equal(51820, resolved.TargetPort);
        Assert.Equal(choice, await _catalog.SelectionAsync("manual").ConfigureAwait(false));
        Assert.Equal(revision, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision);
    }

    [Fact]
    public async Task AVerifiedLegacyHelloUpgradesAManualAliasWithoutChangingTheChoiceToDirect()
    {
        var target = await ConfigAsync("old", Key("server"), 51820).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("old", true,
            WebSocketHost: "wss://relay.example.net:9443/manual")).ConfigureAwait(false);
        var original = await _catalog.SelectionAsync("old").ConfigureAwait(false);

        await _catalog.SynchronizeAsync("old", target, LegacyOffer(8443, "offered"), 51820).ConfigureAwait(false);
        var choice = await _catalog.SelectionAsync("old").ConfigureAwait(false);
        var resolved = await _catalog.ResolveAsync("old").ConfigureAwait(false);

        Assert.NotNull(choice.Reference);
        Assert.NotEqual(original.Reference, choice.Reference);
        Assert.Equal(new ProxyReference(ProxySourceIdentity.Legacy(target.ServerKey), "legacy"), choice.Reference);
        Assert.NotNull(resolved);
        Assert.True(resolved.Record.LegacyOffered);
        Assert.Equal("offered", resolved.Record.Proxy.Path);
        Assert.Empty((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Locals);
    }

    [Fact]
    public async Task AStoredLegacyOfferKeepsItsOriginalUdpDestinationRestriction()
    {
        var target = await ConfigAsync("old", Key("server"), 51820).ConfigureAwait(false);
        await ConfigAsync("other-port", target.ServerKey, 51821).ConfigureAwait(false);
        await ConfigAsync("other-server", Key("different-server"), 51820).ConfigureAwait(false);
        var text = await _store.GetConfigTextAsync("old").ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("old", true)).ConfigureAwait(false);
        await ServerOfferStore.WriteAsync(_store, "old", target, LegacyOffer(8443, "legacy"), Updated).ConfigureAwait(false);

        var choice = await _catalog.SelectionAsync("old").ConfigureAwait(false);
        var resolved = await _catalog.ResolveAsync("old").ConfigureAwait(false);
        Assert.NotNull(resolved);
        Assert.True(resolved.Record.LegacyOffered);
        Assert.Equal(51820, resolved.TargetPort);
        Assert.Equal(text, await _store.GetConfigTextAsync("old").ConfigureAwait(false));

        await FaultAsync("proxy-legacy-target", () => _catalog.SelectAsync("other-port", choice.Reference)).ConfigureAwait(false);
        await FaultAsync("proxy-legacy-target", () => _catalog.SelectAsync("other-server", choice.Reference)).ConfigureAwait(false);
        var state = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.True(await _store.TrySaveTransportProxyStateAsync(state with
        {
            Revision = state.Revision + 1,
            Selections = state.Selections.Where(one => one.Config is not ("other-port" or "other-server"))
                .Concat([new TransportProxySelection("other-port", choice.Reference),
                    new TransportProxySelection("other-server", choice.Reference)]).ToArray(),
        }, state.Revision).ConfigureAwait(false));
        await FaultAsync("proxy-legacy-target", () => _catalog.ResolveAsync("other-port")).ConfigureAwait(false);
        await FaultAsync("proxy-legacy-target", () => _catalog.ResolveAsync("other-server")).ConfigureAwait(false);
        Assert.Equal(choice.Reference, (await _catalog.SelectionAsync("other-port").ConfigureAwait(false)).Reference);
        Assert.Equal(choice.Reference, (await _catalog.SelectionAsync("other-server").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task AnOffLegacyChoiceStaysExplicitNoneWhenTheServerAdvertisesAFront()
    {
        var target = await ConfigAsync("direct", Key("server")).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("direct", false)).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("direct", target, LegacyOffer(8443, "legacy"), 51820).ConfigureAwait(false);

        Assert.Null((await _catalog.SelectionAsync("direct").ConfigureAwait(false)).Reference);
        Assert.Null(await _catalog.ResolveAsync("direct").ConfigureAwait(false));
        Assert.Single(await _catalog.ListAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task ASelectedLegacyFrontMissingFromHelloDoesNotFallBackToUdp()
    {
        var target = await ConfigAsync("old", Key("server")).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("old", true)).ConfigureAwait(false);
        await ServerOfferStore.WriteAsync(_store, "old", target, new ServerOffer("1", "phone", new Dictionary<string, JsonElement>()), Updated).ConfigureAwait(false);

        Assert.NotNull((await _catalog.SelectionAsync("old").ConfigureAwait(false)).Reference);
        await FaultAsync("proxy-missing", () => _catalog.ResolveAsync("old")).ConfigureAwait(false);
    }

    [Fact]
    public async Task TwoSignedWgIssuersShareMetadataWithoutOverwritingTheirCredentials()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = await ConfigAsync("a", Key("server-a"), 51820).ConfigureAwait(false);
        var second = await ConfigAsync("b", Key("server-b"), 51821).ConfigureAwait(false);
        var consumer = await ConfigAsync("c", Key("server-c"), 51822).ConfigureAwait(false);
        var reference = SignedReference("one");
        await _catalog.SynchronizeAsync("a", first, SignedOffer(key, first.ServerKey, [Proxy("one", "credential-a")]), 51820).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("b", second, SignedOffer(key, second.ServerKey, [Proxy("one", "credential-b")]), 51821).ConfigureAwait(false);
        await _catalog.SelectAsync("a", reference).ConfigureAwait(false);
        await _catalog.SelectAsync("b", reference).ConfigureAwait(false);
        await _catalog.SelectAsync("c", reference).ConfigureAwait(false);

        Assert.Equal("credential-a", (await _catalog.ResolveAsync("a").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
        Assert.Equal("credential-b", (await _catalog.ResolveAsync("b").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
        var external = await _catalog.ResolveAsync("c").ConfigureAwait(false);
        Assert.NotNull(external);
        Assert.Equal("a", external.Publisher);
        Assert.Equal(consumer.Host, external.TargetHost);
        Assert.Equal(51822, external.TargetPort);
        Assert.Equal("cdn.example.net", external.Record.Proxy.Host);
        var summary = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false));
        Assert.Equal(reference, summary.Reference);
        Assert.Equal(new[] { first.Host, second.Host }, summary.Sources.OrderBy(one => one, StringComparer.Ordinal));
        var held = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.Single(held.Trust.Where(one => one.Kind == "signed"));
        Assert.Equal(new[] { "credential-a", "credential-b" }, held.Publications.Where(one => one.Slot == "catalog")
            .SelectMany(one => one.Records).Select(one => one.Proxy.Auth.Credential).OrderBy(one => one, StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheSameUnsignedUuidFromDifferentIssuersProducesDifferentEntries()
    {
        var first = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        var second = await ConfigAsync("b", Key("server-b")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", first, Offer(new OfferedProxyCatalog(1, Source, [Proxy("one", "credential-a")]))).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("b", second, Offer(new OfferedProxyCatalog(1, Source, [Proxy("one", "credential-b")]))).ConfigureAwait(false);

        var entries = await _catalog.ListAsync().ConfigureAwait(false);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, one => one.Reference == new ProxyReference(ProxySourceIdentity.Unsigned(Source, first.ServerKey), "one"));
        Assert.Contains(entries, one => one.Reference == new ProxyReference(ProxySourceIdentity.Unsigned(Source, second.ServerKey), "one"));
        Assert.Equal(2, entries.Select(one => one.Reference.Source).Distinct().Count());
    }

    [Fact]
    public async Task AVerifiedSignedUpgradeCarriesSelectionsToTheSignedNamespace()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        await ConfigAsync("consumer", Key("server-b")).ConfigureAwait(false);
        var oldReference = new ProxyReference(ProxySourceIdentity.Unsigned(Source, publisher.ServerKey), "one");
        await _catalog.SynchronizeAsync("a", publisher, Offer(new OfferedProxyCatalog(1, Source, [Proxy("one")]))).ConfigureAwait(false);
        await _catalog.SelectAsync("consumer", oldReference).ConfigureAwait(false);

        await _catalog.SynchronizeAsync("a", publisher, SignedOffer(key, publisher.ServerKey, [Proxy("one")])).ConfigureAwait(false);

        Assert.Equal(SignedReference("one"), (await _catalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
        Assert.Equal(SignedReference("one"), (await _catalog.ResolveAsync("consumer").ConfigureAwait(false))!.Record.Reference);
    }

    [Fact]
    public async Task AnotherPublishersSignedOfferDoesNotUpgradeAnUnsignedSelection()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        var second = await ConfigAsync("b", Key("server-b")).ConfigureAwait(false);
        await ConfigAsync("consumer", Key("server-c")).ConfigureAwait(false);
        var original = new ProxyReference(ProxySourceIdentity.Unsigned(Source, first.ServerKey), "one");
        await _catalog.SynchronizeAsync("a", first, Offer(new OfferedProxyCatalog(1, Source, [Proxy("one", "original")]))).ConfigureAwait(false);
        await _catalog.SelectAsync("consumer", original).ConfigureAwait(false);

        await _catalog.SynchronizeAsync("b", second, SignedOffer(key, second.ServerKey, [Proxy("one", "other")])).ConfigureAwait(false);

        Assert.Equal(original, (await _catalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
        Assert.Equal("original", (await _catalog.ResolveAsync("consumer").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
    }

    [Fact]
    public async Task ReusingAConfigurationNameWithAnotherClientKeyDoesNotUpgradeItsOldUnsignedReference()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        await ConfigAsync("consumer", Key("server-b")).ConfigureAwait(false);
        var original = new ProxyReference(ProxySourceIdentity.Unsigned(Source, publisher.ServerKey), "one");
        await _catalog.SynchronizeAsync("a", publisher, Offer(new OfferedProxyCatalog(1, Source, [Proxy("one")]))).ConfigureAwait(false);
        await _catalog.SelectAsync("consumer", original).ConfigureAwait(false);
        var text = (await _store.GetConfigTextAsync("a").ConfigureAwait(false))!
            .Replace(publisher.PrivateKey, Key("replacement-client"), StringComparison.Ordinal);
        await _store.SaveConfigAsync("a", text).ConfigureAwait(false);
        var changed = ConfigServices.Target(text)!;

        await _catalog.SynchronizeAsync("a", changed, SignedOffer(key, changed.ServerKey, [Proxy("one")])).ConfigureAwait(false);

        Assert.Equal(original, (await _catalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
        await FaultAsync("proxy-missing", () => _catalog.ResolveAsync("consumer")).ConfigureAwait(false);
    }

    [Fact]
    public async Task AChangedPublisherIdentityCannotKeepServingItsStaleCredential()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        await ConfigAsync("consumer", Key("server-b")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", publisher, SignedOffer(key, publisher.ServerKey, [Proxy("one")])).ConfigureAwait(false);
        await _catalog.SelectAsync("consumer", SignedReference("one")).ConfigureAwait(false);
        var text = (await _store.GetConfigTextAsync("a").ConfigureAwait(false))!
            .Replace(publisher.ServerKey, Key("replacement-server"), StringComparison.Ordinal);

        await _store.SaveConfigAsync("a", text).ConfigureAwait(false);

        Assert.Equal(SignedReference("one"), (await _catalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
        await FaultAsync("proxy-missing", () => _catalog.ResolveAsync("consumer")).ConfigureAwait(false);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"source\":\"b4ca7082-c757-41cc-8094-1a3c0048375c\",\"items\":[]}")]
    public async Task MalformedAndUnsupportedCatalogsRetainTheLastSnapshot(string payload)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", publisher, SignedOffer(key, publisher.ServerKey, [Proxy("one", "retained")])).ConfigureAwait(false);
        await _catalog.SelectAsync("a", SignedReference("one")).ConfigureAwait(false);
        var malformed = new ServerOffer("1", "phone", new Dictionary<string, JsonElement>
        {
            [ServerOffer.ProxiesFeature] = JsonSerializer.Deserialize<JsonElement>(payload),
        });

        await _catalog.SynchronizeAsync("a", publisher, malformed).ConfigureAwait(false);

        Assert.Equal("retained", (await _catalog.ResolveAsync("a").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
        Assert.Equal("proxy-catalog-invalid", (await PublicationAsync("a").ConfigureAwait(false)).Error);
    }

    [Fact]
    public async Task AnInvalidSignatureDoesNotWithdrawOrReplaceThePreviousSnapshot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        var valid = SignedCatalog(key, Source, publisher.ServerKey, [Proxy("one", "retained")]);
        await _catalog.SynchronizeAsync("a", publisher, Offer(valid)).ConfigureAwait(false);
        await _catalog.SelectAsync("a", SignedReference("one")).ConfigureAwait(false);
        var invalid = valid with { Items = [], Proof = valid.Proof! with { Signature = Convert.ToBase64String(new byte[64]) } };

        await _catalog.SynchronizeAsync("a", publisher, Offer(invalid)).ConfigureAwait(false);

        Assert.Equal("retained", (await _catalog.ResolveAsync("a").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
        Assert.Equal("proxy-catalog-invalid", (await PublicationAsync("a").ConfigureAwait(false)).Error);
    }

    [Fact]
    public async Task AProofCopiedFromAnotherWgIssuerDoesNotOverwriteTheReceivingPublication()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        var second = await ConfigAsync("b", Key("server-b")).ConfigureAwait(false);
        var firstOffer = SignedOffer(key, first.ServerKey, [Proxy("one", "credential-a")]);
        await _catalog.SynchronizeAsync("a", first, firstOffer).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("b", second, SignedOffer(key, second.ServerKey, [Proxy("one", "credential-b")])).ConfigureAwait(false);
        await _catalog.SelectAsync("b", SignedReference("one")).ConfigureAwait(false);

        await _catalog.SynchronizeAsync("b", second, firstOffer).ConfigureAwait(false);

        Assert.Equal("credential-b", (await _catalog.ResolveAsync("b").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
        Assert.Equal("proxy-source-invalid", (await PublicationAsync("b").ConfigureAwait(false)).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnsignedOrAbsentCatalogCannotDowngradeAPinnedSource(bool absent)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", publisher, SignedOffer(key, publisher.ServerKey, [Proxy("one", "retained")])).ConfigureAwait(false);
        await _catalog.SelectAsync("a", SignedReference("one")).ConfigureAwait(false);
        var downgrade = absent ? LegacyOffer(8443, "legacy") : Offer(new OfferedProxyCatalog(1, Source, []));

        await _catalog.SynchronizeAsync("a", publisher, downgrade).ConfigureAwait(false);

        Assert.Equal("retained", (await _catalog.ResolveAsync("a").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
        Assert.Equal("proxy-source-downgrade", (await PublicationAsync("a").ConfigureAwait(false)).Error);
        Assert.Equal(SignedReference("one"), (await _catalog.SelectionAsync("a").ConfigureAwait(false)).Reference);
    }

    [Fact]
    public async Task AConflictingSourceKeyCannotReplaceThePinnedKeyOrMetadata()
    {
        using var original = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var different = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", publisher, SignedOffer(original, publisher.ServerKey, [Proxy("one", "retained")])).ConfigureAwait(false);
        await _catalog.SelectAsync("a", SignedReference("one")).ConfigureAwait(false);
        var changed = Proxy("one", "replacement") with { Host = "other.example.net", Updated = Updated.AddDays(1) };

        await _catalog.SynchronizeAsync("a", publisher, SignedOffer(different, publisher.ServerKey, [changed])).ConfigureAwait(false);

        var resolved = await _catalog.ResolveAsync("a").ConfigureAwait(false);
        Assert.Equal("retained", resolved!.Record.Proxy.Auth.Credential);
        Assert.Equal("cdn.example.net", resolved.Record.Proxy.Host);
        Assert.Equal("proxy-source-conflict", (await PublicationAsync("a").ConfigureAwait(false)).Error);
        Assert.Equal(Convert.ToBase64String(original.ExportSubjectPublicKeyInfo()), Assert.Single(
            (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Trust.Where(one => one.Kind == "signed")).PublicKey);
    }

    [Fact]
    public async Task RenewingTheIdentitySignatureDoesNotAdvanceTheStoredRevision()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", publisher, SignedOffer(key, publisher.ServerKey, [Proxy("one")])).ConfigureAwait(false);
        var revision = (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision;

        Assert.False(await _catalog.SynchronizeAsync("a", publisher, SignedOffer(key, publisher.ServerKey, [Proxy("one")])).ConfigureAwait(false));

        Assert.Equal(revision, (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Revision);
    }

    [Fact]
    public async Task WithdrawalsRetainTheSelectionUntilTheUserExplicitlyChoosesNone()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        var second = await ConfigAsync("b", Key("server-b")).ConfigureAwait(false);
        await ConfigAsync("consumer", Key("server-c")).ConfigureAwait(false);
        var reference = SignedReference("one");
        await _catalog.SynchronizeAsync("a", first, SignedOffer(key, first.ServerKey, [Proxy("one", "credential-a")])).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("b", second, SignedOffer(key, second.ServerKey, [Proxy("one", "credential-b")])).ConfigureAwait(false);
        await _catalog.SelectAsync("consumer", reference).ConfigureAwait(false);

        await _catalog.SynchronizeAsync("a", first, SignedOffer(key, first.ServerKey, [])).ConfigureAwait(false);
        Assert.Equal("credential-b", (await _catalog.ResolveAsync("consumer").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
        Assert.False(Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Missing);

        await _catalog.SynchronizeAsync("b", second, SignedOffer(key, second.ServerKey, [])).ConfigureAwait(false);
        Assert.Equal(reference, (await _catalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
        await FaultAsync("proxy-missing", () => _catalog.ResolveAsync("consumer")).ConfigureAwait(false);
        var missing = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false));
        Assert.True(missing.Missing);
        Assert.Equal("Proxy one", missing.Name);
        Assert.Equal(TransportProxyAvailability.Unavailable, missing.Availability);
        Assert.Single((await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Trust.Where(one => one.Kind == "signed"));

        using var differentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await _catalog.SynchronizeAsync("a", first, SignedOffer(differentKey, first.ServerKey, [Proxy("one", "replacement")])).ConfigureAwait(false);
        Assert.Equal("proxy-source-conflict", (await PublicationAsync("a").ConfigureAwait(false)).Error);
        await FaultAsync("proxy-missing", () => _catalog.ResolveAsync("consumer")).ConfigureAwait(false);

        await _catalog.SelectAsync("consumer", null).ConfigureAwait(false);
        Assert.Null(await _catalog.ResolveAsync("consumer").ConfigureAwait(false));
    }

    [Fact]
    public async Task RemovingALocalEntryRetainsItsMissingReferenceAndOtherChoices()
    {
        await ConfigAsync("carried", Key("server-a")).ConfigureAwait(false);
        await ConfigAsync("direct", Key("server-b")).ConfigureAwait(false);
        var reference = await _catalog.SaveLocalAsync(null, Proxy("draft")).ConfigureAwait(false);
        await _catalog.SelectAsync("carried", reference).ConfigureAwait(false);
        await _catalog.SelectAsync("direct", null).ConfigureAwait(false);

        await _catalog.RemoveLocalAsync(reference).ConfigureAwait(false);

        Assert.Equal(reference, (await _catalog.SelectionAsync("carried").ConfigureAwait(false)).Reference);
        await FaultAsync("proxy-missing", () => _catalog.ResolveAsync("carried")).ConfigureAwait(false);
        Assert.Null(await _catalog.ResolveAsync("direct").ConfigureAwait(false));
        var missing = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false));
        Assert.True(missing.Local);
        Assert.True(missing.Missing);
    }

    [Fact]
    public async Task RenameAndDeletionFollowThePublicationWithoutRemovingSourceTrust()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        await ConfigAsync("consumer", Key("server-b")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", publisher, SignedOffer(key, publisher.ServerKey, [Proxy("one")])).ConfigureAwait(false);
        await _catalog.SelectAsync("a", SignedReference("one")).ConfigureAwait(false);
        await _catalog.SelectAsync("consumer", SignedReference("one")).ConfigureAwait(false);

        await _store.RenameConfigAsync("a", "renamed").ConfigureAwait(false);
        await _catalog.RenameAsync("a", "renamed").ConfigureAwait(false);
        Assert.Equal("renamed", (await _catalog.ResolveAsync("consumer").ConfigureAwait(false))!.Publisher);
        Assert.Equal(SignedReference("one"), (await _catalog.SelectionAsync("renamed").ConfigureAwait(false)).Reference);

        await _store.RemoveConfigAsync("renamed").ConfigureAwait(false);
        await _catalog.ForgetAsync("renamed").ConfigureAwait(false);
        var state = await _store.GetTransportProxyStateAsync().ConfigureAwait(false);
        Assert.DoesNotContain(state.Publications, one => one.Publisher is "a" or "renamed");
        Assert.DoesNotContain(state.Selections, one => one.Config is "a" or "renamed");
        Assert.Single(state.Trust.Where(one => one.Kind == "signed"));
        Assert.Equal(SignedReference("one"), (await _catalog.SelectionAsync("consumer").ConfigureAwait(false)).Reference);
        await FaultAsync("proxy-missing", () => _catalog.ResolveAsync("consumer")).ConfigureAwait(false);
    }

    [Fact]
    public async Task SignedRawSourceCannotAliasTheLocalNamespace()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publisher = await ConfigAsync("a", Key("server-a")).ConfigureAwait(false);
        var local = await _catalog.SaveLocalAsync(null, Proxy("draft", "local-credential")).ConfigureAwait(false);
        await _catalog.SelectAsync("a", local).ConfigureAwait(false);
        var remote = new ProxyReference(ProxySourceIdentity.Signed(local.Source), local.Id);

        await _catalog.SynchronizeAsync("a", publisher,
            Offer(SignedCatalog(key, local.Source, publisher.ServerKey, [Proxy(local.Id, "remote-credential")]))).ConfigureAwait(false);

        var entries = await _catalog.ListAsync().ConfigureAwait(false);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, one => one.Reference == local && one.Local);
        Assert.Contains(entries, one => one.Reference == remote && !one.Local);
        Assert.Equal("local-credential", (await _catalog.ResolveAsync("a").ConfigureAwait(false))!.Record.Proxy.Auth.Credential);
    }

    [Fact]
    public async Task AvailabilityStartsUnknownAndAnEditedDefinitionInvalidatesItsProbe()
    {
        var reference = await _catalog.SaveLocalAsync(null, Proxy("draft")).ConfigureAwait(false);
        Assert.Equal(TransportProxyAvailability.Unknown, Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Availability);
        await _catalog.ProbeAsync(reference, (_, _) => Task.FromResult(new TransportProxyProbeResult(TransportProxyAvailability.Available))).ConfigureAwait(false);
        Assert.Equal(TransportProxyAvailability.Available, Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Availability);

        await _catalog.SaveLocalAsync(reference.Id, Proxy(reference.Id) with { Host = "changed.example.net" }).ConfigureAwait(false);

        Assert.Equal(TransportProxyAvailability.Unknown, Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Availability);
    }

    [Fact]
    public async Task AWithdrawnSourceSurvivesRestartInTheMissingSelection()
    {
        var target = await ConfigAsync("a", Key("issuer")).ConfigureAwait(false);
        var consumer = await ConfigAsync("b", Key("another-issuer")).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await _catalog.SynchronizeAsync("a", target, SignedOffer(key, target.ServerKey, [Proxy("one")])).ConfigureAwait(false);
        await _catalog.SelectAsync("b", SignedReference("one")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", target, SignedOffer(key, target.ServerKey, [])).ConfigureAwait(false);

        var restarted = new TransportProxyCatalog(new SqliteStateStore(_path));
        var missing = Assert.Single(await restarted.ListAsync().ConfigureAwait(false));

        Assert.True(missing.Missing);
        Assert.Equal([target.Host], missing.Sources);
        Assert.DoesNotContain("credential", JsonSerializer.Serialize(missing, Web), StringComparison.Ordinal);
        Assert.NotEqual(target.ServerKey, consumer.ServerKey);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task AnEquivalentLegacyAliasIsShownOnlyWhileSelected(bool useLegacy, int expected)
    {
        var target = await ConfigAsync("a", Key("issuer")).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("a", useLegacy)).ConfigureAwait(false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var modern = SignedOffer(key, target.ServerKey, [Proxy("one") with { Host = target.Host, Port = 9443 }]);
        var features = modern.Features.ToDictionary(one => one.Key, one => one.Value);
        features[ServerOffer.WebSocketFeature] = JsonSerializer.SerializeToElement(new { port = 9443, path = "transport" }, Web);
        var offer = new ServerOffer("1", "phone", features);
        await ServerOfferStore.WriteAsync(_store, "a", target, offer, DateTimeOffset.UtcNow).ConfigureAwait(false);

        var records = await _catalog.ListAsync().ConfigureAwait(false);

        Assert.Equal(expected, records.Count);
        Assert.Contains(records, one => one.Reference == SignedReference("one"));
        var selection = await _catalog.SelectionAsync("a").ConfigureAwait(false);
        Assert.Equal(useLegacy, selection.Reference is not null);
    }

    [Fact]
    public async Task PublicAliasesFollowOnlyTheVerifiedUnsignedToSignedUpgrade()
    {
        var target = await ConfigAsync("a", Key("issuer")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", target, Offer(new OfferedProxyCatalog(1, Source, [Proxy("one")]))).ConfigureAwait(false);
        var old = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Reference;
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await _catalog.SynchronizeAsync("a", target, SignedOffer(signer, target.ServerKey, [Proxy("one")])).ConfigureAwait(false);

        var upgraded = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false));
        Assert.Equal(SignedReference("one"), upgraded.Reference);
        Assert.Equal([old], upgraded.Aliases);
        Assert.DoesNotContain("credential", JsonSerializer.Serialize(upgraded, Web), StringComparison.OrdinalIgnoreCase);

        await _catalog.SynchronizeAsync("a", target, SignedOffer(signer, target.ServerKey, [])).ConfigureAwait(false);
        Assert.DoesNotContain(await _catalog.ListAsync().ConfigureAwait(false), one => one.Aliases?.Contains(old) == true);
    }

    [Fact]
    public async Task ForgedSourceAndChangedPublisherIdentityDoNotCreatePublicAliases()
    {
        var target = await ConfigAsync("a", Key("issuer")).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("a", target, Offer(new OfferedProxyCatalog(1, Source, [Proxy("one")]))).ConfigureAwait(false);
        var old = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Reference;
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await _catalog.SynchronizeAsync("a", target, SignedOffer(signer, Key("wrong-issuer"), [Proxy("one")])).ConfigureAwait(false);
        Assert.DoesNotContain(await _catalog.ListAsync().ConfigureAwait(false), one => one.Aliases?.Contains(old) == true);
        await _catalog.SynchronizeAsync("a", target, SignedOffer(signer, target.ServerKey, [Proxy("one")])).ConfigureAwait(false);
        await ConfigAsync("a", Key("replacement-issuer")).ConfigureAwait(false);
        Assert.DoesNotContain(await _catalog.ListAsync().ConfigureAwait(false), one => one.Aliases?.Contains(old) == true);
    }

    [Fact]
    public async Task OnlyASignedProtocolProofUpgradesAnUnversionedGetRecord()
    {
        var legacy = Proxy("get") with
        {
            Transport = "xhttp", Auth = new OfferedProxyAuth("vless", "22222222-2222-2222-2222-222222222222"),
            Xhttp = new OfferedXHttp("packet-up", "GET", "header", Scheme: "http"),
        };
        var reference = await _catalog.SaveLocalAsync(null, legacy).ConfigureAwait(false);
        await _catalog.ProbeAsync(reference, (_, _) => Task.FromResult(new TransportProxyProbeResult(TransportProxyAvailability.Available))).ConfigureAwait(false);
        Assert.Equal("xhttp", (await _catalog.LocalAsync(reference).ConfigureAwait(false))!.Proxy.Transport);
        await _catalog.ProbeAsync(reference, (_, _) => Task.FromResult(new TransportProxyProbeResult(TransportProxyAvailability.Available, ProtocolVersion: 2))).ConfigureAwait(false);
        var after = (await _catalog.LocalAsync(reference).ConfigureAwait(false))!;
        Assert.Equal(reference, after.Reference);
        Assert.Equal("http", after.Proxy.Transport);
        Assert.Equal(legacy.Auth.Credential, after.Proxy.Auth.Credential);
        Assert.False(after.Proxy.Tls.Enabled);
        await _catalog.SaveLocalAsync(reference.Id, legacy with { Name = "Renamed" }).ConfigureAwait(false);
        Assert.Equal("http", (await _catalog.LocalAsync(reference).ConfigureAwait(false))!.Proxy.Transport);
    }

    [Fact]
    public async Task ModernHelloPreservesSelectionAndDestinationRestrictions()
    {
        var target = await ConfigAsync("modern", Key("issuer")).ConfigureAwait(false);
        var item = ProxyTransportModesTests.Http("POST", false) with
        {
            Targets = new ProxyTargets(true, [new ProxyDestination(target.Host, 51820)]),
        };
        var feature = new OfferedProxyCatalog(2, Source, [item]);
        var offer = new ServerOffer("new", "phone", new Dictionary<string, JsonElement>
        {
            [ServerOffer.ProxiesV2Feature] = JsonSerializer.SerializeToElement(feature, Web),
        });
        await _catalog.SynchronizeAsync("modern", target, offer).ConfigureAwait(false);
        var reference = Assert.Single(await _catalog.ListAsync().ConfigureAwait(false)).Reference;
        await _catalog.SelectAsync("modern", reference).ConfigureAwait(false);
        await _catalog.SynchronizeAsync("modern", target, offer).ConfigureAwait(false);
        var selected = (await _catalog.ResolveAsync("modern").ConfigureAwait(false))!;
        Assert.Equal(reference, selected.Record.Reference);
        Assert.Equal("POST", selected.Record.Proxy.Http!.Method);
        Assert.False(selected.Record.Proxy.Tls.Enabled);
        Assert.Equal(target.Host, selected.TargetHost);
        Assert.Equal(51820, selected.TargetPort);
        Assert.True(selected.Record.Proxy.Targets!.Restricted);
        Assert.Equal(reference, (await _catalog.SelectionAsync("modern").ConfigureAwait(false)).Reference);
    }

    private async Task<ServiceTarget> ConfigAsync(string name, string issuer, int port = 51820)
    {
        var text = $"""
                    [Interface]
                    PrivateKey = {Key("client-" + name)}
                    Address = 10.8.0.2/32

                    [Peer]
                    PublicKey = {issuer}
                    Endpoint = vpn-{name}.example.net:{port}
                    AllowedIPs = 0.0.0.0/0
                    """;
        await _store.SaveConfigAsync(name, text).ConfigureAwait(false);
        return ConfigServices.Target(text)!;
    }

    private async Task<TransportProxyPublication> PublicationAsync(string name) => Assert.Single(
        (await _store.GetTransportProxyStateAsync().ConfigureAwait(false)).Publications.Where(one => one.Publisher == name && one.Slot == "catalog"));

    private static async Task FaultAsync(string code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<TransportProxyFault>(action).ConfigureAwait(false);
        Assert.Equal(code, error.Code);
    }

    private static string Key(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ProxyReference SignedReference(string id) => new(ProxySourceIdentity.Signed(Source), id);

    private static OfferedProxy Proxy(string id, string credential = "credential") => new(id, "Proxy " + id,
        "websocket", "cdn.example.net", 443, "transport", new OfferedProxyTls("cdn.example.net", ""),
        new OfferedProxyAuth("bearer", credential), Updated);

    private static ServerOffer LegacyOffer(int port, string path) => new("1", "phone", new Dictionary<string, JsonElement>
    {
        [ServerOffer.WebSocketFeature] = JsonSerializer.SerializeToElement(new { port, path }, Web),
    });

    private static ServerOffer Offer(OfferedProxyCatalog catalog) => new("1", "phone", new Dictionary<string, JsonElement>
    {
        [ServerOffer.ProxiesFeature] = JsonSerializer.SerializeToElement(catalog, Web),
    });

    private static ServerOffer SignedOffer(ECDsa key, string issuer, IReadOnlyList<OfferedProxy> items) =>
        Offer(SignedCatalog(key, Source, issuer, items));

    private static OfferedProxyCatalog SignedCatalog(ECDsa key, string source, string issuer, IReadOnlyList<OfferedProxy> items)
    {
        var message = Encoding.UTF8.GetBytes("amneziageo.proxy-source.v1\n" + Guid.Parse(source).ToString("D") + "\n" + issuer);
        var proof = new OfferedProxySourceProof(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), issuer,
            Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        return new OfferedProxyCatalog(1, source, items, proof);
    }
}
