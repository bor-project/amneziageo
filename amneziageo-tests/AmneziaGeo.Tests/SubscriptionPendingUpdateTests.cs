using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed partial class SubscriptionRefreshTests
{
    [Fact]
    public async Task MatchingHelloClearsAnObsoleteUpdateWithoutFetching()
    {
        var name = await PendingPhoneAsync().ConfigureAwait(false);
        var text = _library.Text(name);
        await HelloRevisionAsync(name, text).ConfigureAwait(false);
        _feed.Status = System.Net.HttpStatusCode.ServiceUnavailable;

        await PendingService().CheckForConfigAsync(name, default).ConfigureAwait(false);

        Assert.Null(await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false));
        Assert.Equal(text, _library.Text(name));
    }

    [Fact]
    public async Task MatchingPendingHelloRechecksTheLocallyEditedBase()
    {
        var name = await PendingPhoneAsync().ConfigureAwait(false);
        await HelloRevisionAsync(name, Config("phone", "second.example:1234")).ConfigureAwait(false);
        var edited = _library.Text(name) + "\nMTU = 1280";
        await _library.EditAsync(name, edited, default).ConfigureAwait(false);

        await PendingService().CheckForConfigAsync(name, default).ConfigureAwait(false);

        var pending = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        Assert.Equal(ConfigurationRevision.Of(edited), pending!.BaseRevision);
        Assert.Equal(edited, _library.Text(name));
    }

    [Fact]
    public async Task LocalEditRejectsOldConfirmationAndPreparesTheNextAttempt()
    {
        var name = await PendingPhoneAsync().ConfigureAwait(false);
        var pending = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        var edited = _library.Text(name) + "\n# local edit";
        await _library.EditAsync(name, edited, default).ConfigureAwait(false);

        var rejected = await _refresher.ApplyAsync(await Stored().ConfigureAwait(false), name, pending!.Revision, default).ConfigureAwait(false);

        Assert.False(rejected.Ok);
        Assert.Equal("configuration-update-stale", rejected.Error);
        Assert.Equal(edited, _library.Text(name));
        var rebased = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        Assert.Equal(ConfigurationRevision.Of(edited), rebased!.BaseRevision);
        var applied = await _refresher.ApplyAsync(await Stored().ConfigureAwait(false), name, rebased.Revision, default).ConfigureAwait(false);
        Assert.True(applied.Ok, applied.Error);
        Assert.Null(await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false));
    }

    [Fact]
    public async Task ScheduledCheckDropsMissingNodesAndKeepsTheirSettings()
    {
        var name = await PendingPhoneAsync(true).ConfigureAwait(false);
        await _store.SetConfigDnsAsync(new ConfigDns(name, "9.9.9.9")).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport(name, true)).ConfigureAwait(false);
        _feed.Body = Body(Config("laptop"));

        var result = await PendingService().CheckAsync([Fresh().Name], default).ConfigureAwait(false);

        Assert.True(result.Ack.Ok, result.Ack.Message);
        Assert.Equal(1, result.Gone);
        Assert.Equal([name], result.Dropped);
        Assert.DoesNotContain(name, _library.Names);
        Assert.Null(await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false));
        Assert.False(Assert.Single(await _store.ListSubscriptionMembersAsync(Fresh().Name).ConfigureAwait(false), one => one.ConfigName == name).Present);
        Assert.Equal("9.9.9.9", (await _store.GetConfigDnsAsync(name).ConfigureAwait(false))!.Servers);
        Assert.True((await _store.GetConfigTransportAsync(name).ConfigureAwait(false))!.UseWebSocket);
        _feed.Body = Body(Config("phone"), Config("laptop"));
        Assert.True((await _refresher.RefreshAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false)).Ok);
        Assert.Contains(name, _library.Names);
        Assert.Equal("9.9.9.9", (await _store.GetConfigDnsAsync(name).ConfigureAwait(false))!.Servers);
    }

    [Fact]
    public async Task ConfirmingARemovedNodeClearsItsOfferWithoutWritingIt()
    {
        var name = await PendingPhoneAsync(true).ConfigureAwait(false);
        var pending = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        _feed.Body = Body(Config("laptop"));

        var result = await _refresher.ApplyAsync(await Stored().ConfigureAwait(false), name, pending!.Revision, default).ConfigureAwait(false);

        Assert.False(result.Ok);
        Assert.Equal("configuration-update-stale", result.Error);
        Assert.Null(await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false));
    }

    [Fact]
    public async Task ManualRefreshClearsMissingNodeUpdatesEvenForAPlainLibrary()
    {
        var name = await PendingPhoneAsync(true).ConfigureAwait(false);
        _feed.Body = Body(Config("laptop"));

        await _refresher.RefreshAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);

        Assert.Null(await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false));
    }

    [Fact]
    public async Task EmptyOrFailedChecksPreserveMembersAndPendingUpdates()
    {
        var name = await PendingPhoneAsync().ConfigureAwait(false);
        var before = _library.Text(name);
        _feed.Body = Body();
        var empty = await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);
        Assert.False(empty.Ok);
        _feed.Status = System.Net.HttpStatusCode.ServiceUnavailable;
        var failed = await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);
        Assert.False(failed.Ok);
        Assert.Equal(before, _library.Text(name));
        Assert.NotNull(await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false));
        Assert.All(await _store.ListSubscriptionMembersAsync(Fresh().Name).ConfigureAwait(false), one => Assert.True(one.Present));
    }

    private async Task<string> PendingPhoneAsync(bool laptop = false)
    {
        _feed.Body = laptop ? Body(Config("phone"), Config("laptop")) : Body(Config("phone"));
        Assert.True((await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false)).Ok);
        var name = _library.Names[0];
        _feed.Body = laptop ? Body(Config("phone", "second.example:1234"), Config("laptop")) : Body(Config("phone", "second.example:1234"));
        Assert.True((await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false)).Ok);
        return name;
    }

    [Fact]
    public async Task OldBaseConfirmationsCannotApplyAfterTheOfferWasRebased()
    {
        var name = await PendingPhoneAsync().ConfigureAwait(false);
        var pending = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        var edited = _library.Text(name) + "\n# local edit";
        await _library.EditAsync(name, edited, default).ConfigureAwait(false);
        var args = new[] { name, pending!.Revision, pending.BaseRevision };

        var attempts = await Task.WhenAll(PendingService().ApplyAsync(args, default), PendingService().ApplyAsync(args, default)).ConfigureAwait(false);

        Assert.All(attempts, one => Assert.False(one.Ack.Ok));
        Assert.Equal(edited, _library.Text(name));
        var rebased = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        var accepted = await PendingService().ApplyAsync([name, rebased!.Revision, rebased.BaseRevision], default).ConfigureAwait(false);
        Assert.True(accepted.Ack.Ok, accepted.Ack.Message);
    }

    [Fact]
    public async Task HelloCheckReturnsRemovedNodesToTheTunnelController()
    {
        var name = await PendingPhoneAsync(true).ConfigureAwait(false);
        await HelloRevisionAsync(name, Config("phone", "third.example:1234")).ConfigureAwait(false);
        _feed.Body = Body(Config("laptop"));

        var result = await PendingService().CheckForConfigAsync(name, default).ConfigureAwait(false);

        Assert.True(result.Ack.Ok, result.Ack.Message);
        Assert.Equal([name], result.Dropped);
        Assert.Equal(1, result.Gone);
    }

    [Fact]
    public async Task LegacyHelloRechecksALocallyEditedPendingOffer()
    {
        var name = await PendingPhoneAsync().ConfigureAwait(false);
        var edited = _library.Text(name) + "\n# local edit";
        await _library.EditAsync(name, edited, default).ConfigureAwait(false);

        await PendingService().CheckForConfigAsync(name, default).ConfigureAwait(false);

        var pending = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        Assert.Equal(ConfigurationRevision.Of(edited), pending!.BaseRevision);
        Assert.Equal(edited, _library.Text(name));
    }

    private SubscriptionService PendingService()
    {
        return new SubscriptionService(new GeoHttp(new System.Net.Http.HttpClient(_feed), NullLogger<GeoHttp>.Instance), _store, _library);
    }

    [Fact]
    public async Task AFailedSecondRemovalStillReportsTheFirstToTheController()
    {
        _feed.Body = Body(Config("phone"), Config("laptop"), Config("tablet"));
        Assert.True((await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false)).Ok);
        var names = (await _store.ListSubscriptionMembersAsync(Fresh().Name).ConfigureAwait(false)).Where(one => one.ConfigName != "tablet").Select(one => one.ConfigName).ToArray();
        await ConfigurationUpdates.SaveAsync(_store, names[1], _library.Text(names[1]), Config(names[1], "second.example:1234"), default).ConfigureAwait(false);
        _feed.Body = Body(Config("tablet"));
        var service = new SubscriptionService(new GeoHttp(new System.Net.Http.HttpClient(_feed), NullLogger<GeoHttp>.Instance), _store, new FailingDropLibrary(_library, names[1]));

        var result = await service.CheckAsync([Fresh().Name], default).ConfigureAwait(false);

        Assert.False(result.Ack.Ok);
        Assert.Equal([names[0]], result.Dropped);
        Assert.DoesNotContain(names[0], _library.Names);
        Assert.Contains(names[1], _library.Names);
        Assert.NotNull(await ConfigurationUpdates.ReadAsync(_store, names[1], default).ConfigureAwait(false));
        Assert.NotEmpty((await Stored().ConfigureAwait(false)).LastError);
    }

    private sealed class FailingDropLibrary(ISubscriptionLibrary inner, string held) : ISubscriptionLibrary
    {
        public Task<IReadOnlyCollection<string>> NamesAsync(CancellationToken ct) => inner.NamesAsync(ct);
        public Task<string?> TextAsync(string name, CancellationToken ct) => inner.TextAsync(name, ct);
        public Task AddAsync(string name, string text, CancellationToken ct) => inner.AddAsync(name, text, ct);
        public Task EditAsync(string name, string text, CancellationToken ct) => inner.EditAsync(name, text, ct);
        public Task RemoveAsync(string name, CancellationToken ct) => inner.RemoveAsync(name, ct);

        public Task DropAsync(string name, CancellationToken ct)
        {
            if (name == held)
            {
                throw new InvalidOperationException("the configuration is being edited");
            }
            return inner.DropAsync(name, ct);
        }
    }

    private async Task HelloRevisionAsync(string name, string offered)
    {
        var arguments = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            url = Fresh().Url,
            revision = "subscription-offer",
            configurationRevision = ConfigurationRevision.Of(offered),
        });
        var offer = new ServerOffer("1", "phone", new Dictionary<string, System.Text.Json.JsonElement>
        {
            [ServerOffer.SubscriptionFeature] = arguments,
        });
        await ServerOfferStore.WriteAsync(_store, name, ConfigServices.Target(_library.Text(name))!, offer, DateTimeOffset.UtcNow).ConfigureAwait(false);
    }
}
