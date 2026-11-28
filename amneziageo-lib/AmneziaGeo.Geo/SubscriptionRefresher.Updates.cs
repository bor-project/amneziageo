using System.Net.Http;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

public sealed partial class SubscriptionRefresher
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStateStore, SemaphoreSlim> Gates = new();

    private async Task<SubscriptionResult> LockedAsync(Func<Task<SubscriptionResult>> action, CancellationToken ct)
    {
        var gate = Gates.GetValue(store, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Checks configuration updates without applying them.
    /// </summary>
    public Task<SubscriptionResult> CheckAsync(Subscription subscription, CancellationToken ct) =>
        LockedAsync(() => CheckCoreAsync(subscription, ct), ct);

    /// <summary>
    /// Reconciles the configuration revision announced by hello.
    /// </summary>
    public Task<SubscriptionResult> CheckForConfigAsync(Subscription subscription, string config, CancellationToken ct) =>
        LockedAsync(() => CheckRevisionAsync(subscription, config, ct), ct);

    /// <summary>
    /// Applies changes to installed configurations.
    /// </summary>
    public Task<SubscriptionResult> AutoRefreshAsync(string name, DateTimeOffset started, CancellationToken ct) =>
        LockedAsync(() => AutoRefreshCoreAsync(name, started, ct), ct);

    private async Task<SubscriptionResult> AutoRefreshCoreAsync(string name, DateTimeOffset started, CancellationToken ct)
    {
        var subscription = (await store.ListSubscriptionsAsync(ct).ConfigureAwait(false)).FirstOrDefault(one => one.Name == name);
        if (subscription is null || subscription.Fault.Length > 0)
        {
            return new(0, 0, 0, [], subscription?.Fault ?? string.Empty);
        }

        var rewritten = new List<string>();
        try
        {
            var members = (await store.ListSubscriptionMembersAsync(name, ct).ConfigureAwait(false))
                .Where(one => one.Present).ToArray();
            var names = await library.NamesAsync(ct).ConfigureAwait(false);
            var texts = await TextsAsync(members, names, ct).ConfigureAwait(false);
            if (texts.Count == 0)
            {
                return new(0, 0, 0, []);
            }

            var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (config, text) in texts)
            {
                var kept = await ServerOfferStore.KeptAsync(store, config, text, ct).ConfigureAwait(false);
                if (kept is { Heard: true, Misses: 0 } && kept.Asked >= started
                    && kept.Offer.Subscription() is { Fault.Length: 0, ConfigurationRevision.Length: 64 } offer
                    && offer.Url == subscription.Url)
                {
                    revisions.Add(config, offer.ConfigurationRevision);
                }
            }

            if (revisions.Count == texts.Count && texts.All(one => revisions[one.Key] == ConfigurationRevision.Of(one.Value)))
            {
                foreach (var config in texts.Keys)
                {
                    await ConfigurationUpdates.ClearAsync(store, config, ct).ConfigureAwait(false);
                }

                await store.SaveSubscriptionAsync(subscription with
                {
                    CheckedAt = DateTimeOffset.UtcNow,
                    LastError = string.Empty,
                    Revision = subscription.Offered.Length > 0 ? subscription.Offered : subscription.Revision,
                }, ct).ConfigureAwait(false);
                return new(0, 0, 0, []);
            }

            var fetched = await FetchAsync(subscription.Url, subscription.Pin, ct).ConfigureAwait(false);
            if (fetched.Configs.Count == 0)
            {
                throw new InvalidOperationException("the subscription holds no nodes");
            }

            var changes = SubscriptionMerge.Plan(fetched.Configs, members, names, texts)
                .Where(one => one.Kind == SubscriptionChangeKind.Update && texts.ContainsKey(one.ConfigName)).ToArray();
            foreach (var change in changes)
            {
                if (revisions.TryGetValue(change.ConfigName, out var revision)
                    && revision != ConfigurationRevision.Of(change.ConfText))
                {
                    throw new InvalidOperationException("configuration-update-stale");
                }

                if (!SubscriptionMerge.SameText(texts[change.ConfigName], change.ConfText))
                {
                    WgConfigValidator.Validate(change.ConfText);
                }
            }

            foreach (var change in changes)
            {
                ct.ThrowIfCancellationRequested();
                var config = change.ConfigName;
                var current = await library.TextAsync(config, ct).ConfigureAwait(false);
                if (!SubscriptionMerge.SameText(current, texts[config]))
                {
                    throw new InvalidOperationException("configuration-update-stale");
                }

                if (!SubscriptionMerge.SameText(current, change.ConfText))
                {
                    await library.EditAsync(config, change.ConfText, ct).ConfigureAwait(false);
                    rewritten.Add(config);
                }

                await ConfigurationUpdates.ClearAsync(store, config, ct).ConfigureAwait(false);
                if (change.PreviousRemark.Length > 0 && change.PreviousRemark != change.Remark)
                {
                    await store.SaveSubscriptionMemberAsync(new SubscriptionMember(name, change.Remark, config), ct).ConfigureAwait(false);
                    await store.RemoveSubscriptionMemberAsync(name, change.PreviousRemark, ct).ConfigureAwait(false);
                }
            }

            await CheckedAsync(subscription, fetched, changes.Length == texts.Count, ct).ConfigureAwait(false);
            return new(0, rewritten.Count, 0, rewritten);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && rewritten.Count > 0)
        {
            return new(0, rewritten.Count, 0, rewritten);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or OperationCanceledException or TimeoutException or UriFormatException
                or InvalidOperationException or WgConfigFormatException or IOException)
        {
            var reason = ex is WgConfigFormatException ? "configuration-update-invalid" : Reason(ex);
            await CheckFailedAsync(name, reason, ct).ConfigureAwait(false);
            return new(0, rewritten.Count, 0, rewritten, reason);
        }
    }
    private async Task<SubscriptionResult> CheckRevisionAsync(Subscription subscription, string config, CancellationToken ct)
    {
        var text = await library.TextAsync(config, ct).ConfigureAwait(false);
        var specific = (await ServerOfferStore.ReadAsync(store, config, text, ct).ConfigureAwait(false)).Subscription()?.ConfigurationRevision;
        var pending = await ConfigurationUpdates.ReadAsync(store, config, ct).ConfigureAwait(false);
        var current = ConfigurationRevision.Of(text ?? string.Empty);
        if (specific is { Length: 64 })
        {
            if (text is not null && specific == current)
            {
                await ConfigurationUpdates.ClearAsync(store, config, ct).ConfigureAwait(false);
                return new(0, 0, 0, []);
            }
            if (pending?.Revision == specific && pending.BaseRevision == current)
            {
                return new(0, 0, 0, []);
            }
        }
        else if ((pending is null || pending.BaseRevision == current)
            && (!subscription.Stale || subscription.CheckedAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(30)))
        {
            return new(0, 0, 0, []);
        }
        return await CheckCoreAsync(subscription, ct).ConfigureAwait(false);
    }

    private async Task<SubscriptionResult> CheckCoreAsync(Subscription subscription, CancellationToken ct)
    {
        if (subscription.Fault.Length > 0)
        {
            return new(0, 0, 0, [], subscription.Fault);
        }
        try
        {
            var fetched = await FetchAsync(subscription.Url, subscription.Pin, ct).ConfigureAwait(false);
            var result = await ReconcileAsync(subscription, fetched, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                await CheckFailedAsync(subscription.Name, result.Error, ct).ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or UriFormatException or InvalidOperationException)
        {
            var reason = Reason(ex);
            await CheckFailedAsync(subscription.Name, reason, ct).ConfigureAwait(false);
            return new(0, 0, 0, [], reason);
        }
    }

    private async Task<SubscriptionResult> ReconcileAsync(Subscription subscription, SubscriptionSnapshot fetched, CancellationToken ct)
    {
        var members = await store.ListSubscriptionMembersAsync(subscription.Name, ct).ConfigureAwait(false);
        if (fetched.Configs.Count == 0 && members.Count > 0)
        {
            return new(0, 0, 0, [], "the subscription holds no nodes");
        }
        var names = await library.NamesAsync(ct).ConfigureAwait(false);
        var texts = await TextsAsync(members, names, ct).ConfigureAwait(false);
        var plan = SubscriptionMerge.Plan(fetched.Configs, members, names, texts);
        var dropped = new List<string>();
        try
        {
            foreach (var change in plan)
            {
                if (change.Kind == SubscriptionChangeKind.Update && texts.TryGetValue(change.ConfigName, out var current))
                {
                    await ConfigurationUpdates.SaveAsync(store, change.ConfigName, current, change.ConfText, ct).ConfigureAwait(false);
                }
                else if (change.Kind == SubscriptionChangeKind.Gone)
                {
                    if (names.Contains(change.ConfigName, StringComparer.Ordinal))
                    {
                        await library.DropAsync(change.ConfigName, ct).ConfigureAwait(false);
                        dropped.Add(change.ConfigName);
                    }
                    await ConfigurationUpdates.ClearAsync(store, change.ConfigName, ct).ConfigureAwait(false);
                    await store.SaveSubscriptionMemberAsync(new SubscriptionMember(subscription.Name, change.Remark, change.ConfigName, false), ct).ConfigureAwait(false);
                }
            }
            var synchronized = plan.All(one => one.Kind == SubscriptionChangeKind.Gone
                || one.Kind == SubscriptionChangeKind.Update && SubscriptionMerge.SameText(texts.GetValueOrDefault(one.ConfigName), one.ConfText));
            await CheckedAsync(subscription, fetched, synchronized, ct).ConfigureAwait(false);
            return new(0, 0, dropped.Count, []) { Dropped = dropped };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new(0, 0, dropped.Count, [], Reason(ex)) { Dropped = dropped };
        }
    }

    private async Task CheckFailedAsync(string subscription, string reason, CancellationToken ct)
    {
        var latest = (await store.ListSubscriptionsAsync(ct).ConfigureAwait(false)).FirstOrDefault(one => one.Name == subscription);
        if (latest is not null)
        {
            await store.SaveSubscriptionAsync(latest with { CheckedAt = DateTimeOffset.UtcNow, LastError = reason }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies one confirmed configuration revision.
    /// </summary>
    public async Task<SubscriptionResult> ApplyAsync(Subscription subscription, string config, string revision, CancellationToken ct, string? expectedBaseRevision = null)
    {
        var pending = await ConfigurationUpdates.ReadAsync(store, config, ct).ConfigureAwait(false);
        var expectedBase = expectedBaseRevision ?? pending?.BaseRevision;
        return await LockedAsync(() => ApplyCoreAsync(subscription, config, revision, expectedBase, ct), ct).ConfigureAwait(false);
    }

    private async Task<SubscriptionResult> ApplyCoreAsync(Subscription subscription, string config, string revision, string? expectedBase, CancellationToken ct)
    {
        if (subscription.Fault.Length > 0)
        {
            return new(0, 0, 0, [], subscription.Fault);
        }
        var pending = await ConfigurationUpdates.ReadAsync(store, config, ct).ConfigureAwait(false);
        var current = await library.TextAsync(config, ct).ConfigureAwait(false);
        if (pending is null || pending.Revision != revision || pending.BaseRevision != expectedBase)
        {
            return new(0, 0, 0, [], "configuration-update-stale");
        }
        try
        {
            if (current is null || ConfigurationRevision.Of(current) != pending.BaseRevision)
            {
                var checkedUpdate = await CheckCoreAsync(subscription, ct).ConfigureAwait(false);
                return checkedUpdate with { Error = "configuration-update-stale" };
            }
            var fetched = await FetchAsync(subscription.Url, subscription.Pin, ct).ConfigureAwait(false);
            var members = await store.ListSubscriptionMembersAsync(subscription.Name, ct).ConfigureAwait(false);
            var names = await library.NamesAsync(ct).ConfigureAwait(false);
            var texts = await TextsAsync(members, names, ct).ConfigureAwait(false);
            var change = SubscriptionMerge.Plan(fetched.Configs, members, names, texts)
                .SingleOrDefault(one => one.Kind == SubscriptionChangeKind.Update && one.ConfigName == config);
            if (change is null || ConfigurationRevision.Of(change.ConfText) != revision)
            {
                var checkedUpdate = await ReconcileAsync(subscription, fetched, ct).ConfigureAwait(false);
                return checkedUpdate with { Error = "configuration-update-stale" };
            }
            var latest = await library.TextAsync(config, ct).ConfigureAwait(false);
            if (latest is null || ConfigurationRevision.Of(latest) != pending.BaseRevision)
            {
                var checkedUpdate = await ReconcileAsync(subscription, fetched, ct).ConfigureAwait(false);
                return checkedUpdate with { Error = "configuration-update-stale" };
            }
            await library.EditAsync(config, change.ConfText, ct).ConfigureAwait(false);
            await ConfigurationUpdates.ClearAsync(store, config, ct).ConfigureAwait(false);
            if (change.PreviousRemark.Length > 0 && change.PreviousRemark != change.Remark)
            {
                await store.RemoveSubscriptionMemberAsync(subscription.Name, change.PreviousRemark, ct).ConfigureAwait(false);
            }
            await store.SaveSubscriptionMemberAsync(new SubscriptionMember(subscription.Name, change.Remark, config), ct).ConfigureAwait(false);
            var synchronized = SubscriptionMerge.Plan(fetched.Configs, members, names, texts).All(one => one.Kind == SubscriptionChangeKind.Update
                && (one.ConfigName == config || SubscriptionMerge.SameText(texts.GetValueOrDefault(one.ConfigName), one.ConfText)));
            await CheckedAsync(subscription, fetched, synchronized, ct).ConfigureAwait(false);
            return new(0, 1, 0, [config]);
        }
        catch (WgConfigFormatException)
        {
            return new(0, 0, 0, [], "configuration-update-invalid");
        }
        catch (IOException)
        {
            return new(0, 0, 0, [], "configuration-update-write-failed");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or UriFormatException or InvalidOperationException)
        {
            return new(0, 0, 0, [], Reason(ex));
        }
    }
    private async Task CheckedAsync(Subscription subscription, SubscriptionSnapshot fetched, bool synchronized, CancellationToken ct)
    {
        var latest = (await store.ListSubscriptionsAsync(ct).ConfigureAwait(false)).FirstOrDefault(one => one.Name == subscription.Name);
        if (latest is null)
        {
            return;
        }
        await store.SaveSubscriptionAsync(latest with
        {
            CheckedAt = DateTimeOffset.UtcNow, LastError = string.Empty,
            Revision = synchronized && fetched.Revision.Length > 0 ? fetched.Revision : latest.Revision,
            Offered = latest.Offered != subscription.Offered || fetched.Revision.Length == 0 ? latest.Offered : fetched.Revision,
            IntervalHours = fetched.IntervalHours,
        }, ct).ConfigureAwait(false);
    }

}
