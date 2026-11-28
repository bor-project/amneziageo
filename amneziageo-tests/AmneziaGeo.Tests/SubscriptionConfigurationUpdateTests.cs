using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

#pragma warning disable xUnit1030

namespace AmneziaGeo.Tests;

public sealed partial class SubscriptionRefreshTests
{
    [Fact]
    public async Task AutomaticRefreshDownloadsChangedRevisionOnlyOnce()
    {
        _feed.Body = Body(Config("phone"), Config("laptop"));
        await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false);
        var names = _library.Names;
        var changed = Config("phone").Replace("PersistentKeepalive = 25", "PersistentKeepalive = 20");
        var started = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var before = _feed.Requests;
        await HelloRevisionAsync(names[0], changed).ConfigureAwait(false);
        await HelloRevisionAsync(names[1], _library.Text(names[1])).ConfigureAwait(false);
        _feed.Tag = "revision-2";
        _feed.Body = Body(changed, Config("laptop"));

        var updated = await _refresher.AutoRefreshAsync(Fresh().Name, started, default).ConfigureAwait(false);
        var repeated = await _refresher.AutoRefreshAsync(Fresh().Name, started, default).ConfigureAwait(false);

        Assert.True(updated.Ok, updated.Error);
        Assert.True(repeated.Ok, repeated.Error);
        Assert.Equal([names[0]], updated.Rewritten);
        Assert.Empty(repeated.Rewritten);
        Assert.Equal(before + 1, _feed.Requests);
        Assert.True(SubscriptionMerge.SameText(_library.Text(names[0]), changed));
        Assert.Equal("revision-2", (await Stored().ConfigureAwait(false)).Revision);
    }

    [Fact]
    public async Task AutomaticRefreshKeepsProfilesOnInvalidOrStaleDownload()
    {
        _feed.Body = Body(Config("phone"), Config("laptop"));
        await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false);
        var names = _library.Names;
        var originals = names.Select(_library.Text).ToArray();
        var changed = Config("phone", "next.example:1234");
        var invalid = Config("laptop").Replace("PersistentKeepalive = 25", "PersistentKeepalive = invalid");
        _feed.Body = Body(changed, invalid);
        var result = await _refresher.AutoRefreshAsync(Fresh().Name, DateTimeOffset.UtcNow, default).ConfigureAwait(false);
        Assert.False(result.Ok);
        Assert.Equal(originals, names.Select(_library.Text));

        var started = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await HelloRevisionAsync(names[0], changed).ConfigureAwait(false);
        _feed.Body = Body(Config("phone"), Config("laptop"));
        var stale = await _refresher.AutoRefreshAsync(Fresh().Name, started, default).ConfigureAwait(false);
        Assert.Equal("configuration-update-stale", stale.Error);
        Assert.Equal(originals, names.Select(_library.Text));
    }

    [Fact]
    public async Task AutomaticRefreshFallsBackToLegacyFeedAndRetainsLocalOverrides()
    {
        _feed.Body = Body(Config("phone"));
        await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false);
        var name = Assert.Single(_library.Names);
        await _store.SetConfigDnsAsync(new ConfigDns(name, "1.1.1.1")).ConfigureAwait(false);
        await _library.AddAsync("local", Config("local"), default).ConfigureAwait(false);
        _feed.Status = System.Net.HttpStatusCode.ServiceUnavailable;
        var failed = await _refresher.AutoRefreshAsync(Fresh().Name, DateTimeOffset.UtcNow, default).ConfigureAwait(false);
        Assert.False(failed.Ok);
        Assert.Empty(failed.Rewritten);
        Assert.Contains("51821", _library.Text(name));

        _feed.Status = System.Net.HttpStatusCode.OK;
        _feed.Body = Body(Config("phone", "next.example:1234"), Config("another"));
        var result = await _refresher.AutoRefreshAsync(Fresh().Name, DateTimeOffset.UtcNow, default).ConfigureAwait(false);
        Assert.True(result.Ok, result.Error);
        Assert.Equal([name], result.Rewritten);
        Assert.Equal([name, "local"], _library.Names);
        Assert.Equal("1.1.1.1", (await _store.GetConfigDnsAsync(name).ConfigureAwait(false))?.Servers);
        Assert.Contains("next.example:1234", _library.Text(name));
    }

    [Fact]
    public async Task CheckingAllAwgParametersRetainsTheRunningFileUntilConfirmation()
    {
        var original = Config("phone");
        _feed.Body = Body(original);
        Assert.True((await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false)).Ok);
        var name = Assert.Single(_library.Names);
        var before = _library.Text(name);
        var keys = new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5", "HeaderProtectionKey", "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts", "RandomTrailers", "DisableCookies", "FutureSetting" };
        var offered = original.Replace("[Peer]", string.Join('\n', keys.Select(key => key + " = new-value")) + "\n[Peer]");
        _feed.Body = Body(offered);
        var checkedUpdate = await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);
        Assert.True(checkedUpdate.Ok, checkedUpdate.Error);
        Assert.Empty(checkedUpdate.Rewritten);
        Assert.Equal(before, _library.Text(name));
        var pending = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        Assert.NotNull(pending);
        foreach (var key in keys)
        {
            Assert.Contains(pending.Fields, field => field.EndsWith("." + key, StringComparison.OrdinalIgnoreCase));
        }
        var restarted = new SubscriptionRefresher(new GeoHttp(new System.Net.Http.HttpClient(_feed), Microsoft.Extensions.Logging.Abstractions.NullLogger<GeoHttp>.Instance), _store, _library);
        var applied = await restarted.ApplyAsync(await Stored().ConfigureAwait(false), name, pending.Revision, default).ConfigureAwait(false);
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal([name], applied.Rewritten);
        Assert.True(SubscriptionMerge.SameText(_library.Text(name), offered));
        Assert.Null(await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false));
    }

    [Fact]
    public async Task ConfirmingOneConfigurationLeavesOtherUpdatesPending()
    {
        _feed.Body = Body(Config("phone"), Config("laptop"));
        await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false);
        var names = _library.Names.ToArray();
        var untouched = _library.Text(names[1]);
        _feed.Body = Body(Config("phone", "next.example:1234"), Config("laptop", "next.example:1234"));
        await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);
        var pending = await ConfigurationUpdates.ReadAsync(_store, names[0], default).ConfigureAwait(false);
        var result = await _refresher.ApplyAsync(await Stored().ConfigureAwait(false), names[0], pending!.Revision, default).ConfigureAwait(false);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(untouched, _library.Text(names[1]));
        Assert.NotNull(await ConfigurationUpdates.ReadAsync(_store, names[1], default).ConfigureAwait(false));
    }

    [Fact]
    public async Task ANewServerRevisionRequiresAnotherConfirmation()
    {
        _feed.Body = Body(Config("phone"));
        await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false);
        var name = Assert.Single(_library.Names);
        var before = _library.Text(name);
        _feed.Body = Body(Config("phone", "second.example:1234"));
        await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);
        var second = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        _feed.Body = Body(Config("phone", "third.example:1234"));
        var result = await _refresher.ApplyAsync(await Stored().ConfigureAwait(false), name, second!.Revision, default).ConfigureAwait(false);
        Assert.False(result.Ok);
        Assert.Equal("configuration-update-stale", result.Error);
        Assert.Equal(before, _library.Text(name));
        var third = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        Assert.NotEqual(second.Revision, third!.Revision);
    }

    [Fact]
    public async Task LocalEditsAndFailedFetchesDoNotGetOverwritten()
    {
        _feed.Body = Body(Config("phone"));
        await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false);
        var name = Assert.Single(_library.Names);
        _feed.Body = Body(Config("phone", "second.example:1234"));
        await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);
        var pending = await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false);
        var edited = _library.Text(name) + "\n# local edit";
        await _library.EditAsync(name, edited, default).ConfigureAwait(false);
        Assert.False((await _refresher.ApplyAsync(await Stored().ConfigureAwait(false), name, pending!.Revision, default).ConfigureAwait(false)).Ok);
        Assert.Equal(edited, _library.Text(name));
        _feed.Status = System.Net.HttpStatusCode.ServiceUnavailable;
        Assert.False((await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false)).Ok);
        Assert.Equal(edited, _library.Text(name));
        Assert.NotNull(await ConfigurationUpdates.ReadAsync(_store, name, default).ConfigureAwait(false));
    }

    [Fact]
    public async Task FailedChecksKeepNewerSubscriptionMetadata()
    {
        _feed.Body = Body(Config("phone"));
        await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false);
        var before = await Stored().ConfigureAwait(false);
        await _store.SaveSubscriptionAsync(before with { Offered = "newer-offer", Revision = "newer-revision" }, default).ConfigureAwait(false);
        _feed.Status = System.Net.HttpStatusCode.ServiceUnavailable;
        Assert.False((await _refresher.CheckAsync(before, default).ConfigureAwait(false)).Ok);
        var after = await Stored().ConfigureAwait(false);
        Assert.Equal("newer-offer", after.Offered);
        Assert.Equal("newer-revision", after.Revision);
        Assert.NotEmpty(after.LastError);
    }

    [Fact]
    public async Task UnchangedConfigurationsHaveNoPendingUpdateAndManualRefreshClearsIt()
    {
        _feed.Body = Body(Config("phone"), Config("laptop"));
        await _refresher.RefreshAsync(Fresh(), default).ConfigureAwait(false);
        var names = _library.Names.ToArray();
        _feed.Body = Body(Config("phone", "next.example:1234"), Config("laptop"));
        await _refresher.CheckAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);
        Assert.NotNull(await ConfigurationUpdates.ReadAsync(_store, names[0], default).ConfigureAwait(false));
        Assert.Null(await ConfigurationUpdates.ReadAsync(_store, names[1], default).ConfigureAwait(false));
        await _refresher.RefreshAsync(await Stored().ConfigureAwait(false), default).ConfigureAwait(false);
        Assert.Null(await ConfigurationUpdates.ReadAsync(_store, names[0], default).ConfigureAwait(false));
    }
}
