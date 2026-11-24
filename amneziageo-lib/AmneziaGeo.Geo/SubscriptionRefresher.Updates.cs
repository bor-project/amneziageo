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

    private async Task<SubscriptionResult> CheckCoreAsync(Subscription subscription, CancellationToken ct)
    {
        if (subscription.Fault.Length > 0)
        {
            return new(0, 0, 0, [], subscription.Fault);
        }
        try
        {
            var fetched = await FetchAsync(subscription.Url, subscription.Pin, ct).ConfigureAwait(false);
            var members = await store.ListSubscriptionMembersAsync(subscription.Name, ct).ConfigureAwait(false);
            if (fetched.Configs.Count == 0 && members.Count > 0)
            {
                return new(0, 0, 0, [], "the subscription holds no nodes");
            }
            var names = await library.NamesAsync(ct).ConfigureAwait(false);
            var texts = await TextsAsync(members, names, ct).ConfigureAwait(false);
            var plan = SubscriptionMerge.Plan(fetched.Configs, members, names, texts);
            foreach (var change in plan.Where(one => one.Kind == SubscriptionChangeKind.Update))
            {
                if (texts.TryGetValue(change.ConfigName, out var current))
                {
                    await ConfigurationUpdates.SaveAsync(store, change.ConfigName, current, change.ConfText, ct).ConfigureAwait(false);
                }
            }
            var synchronized = plan.All(one => one.Kind == SubscriptionChangeKind.Update
                && SubscriptionMerge.SameText(texts.GetValueOrDefault(one.ConfigName), one.ConfText));
            await CheckedAsync(subscription, fetched, synchronized, ct).ConfigureAwait(false);
            return new(0, 0, 0, []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or UriFormatException or InvalidOperationException)
        {
            var reason = Reason(ex);
            var latest = (await store.ListSubscriptionsAsync(ct).ConfigureAwait(false)).FirstOrDefault(one => one.Name == subscription.Name);
            if (latest is not null)
            {
                await store.SaveSubscriptionAsync(latest with { CheckedAt = DateTimeOffset.UtcNow, LastError = reason }, ct).ConfigureAwait(false);
            }
            return new(0, 0, 0, [], reason);
        }
    }

    /// <summary>
    /// Applies one confirmed configuration revision.
    /// </summary>
    public Task<SubscriptionResult> ApplyAsync(Subscription subscription, string config, string revision, CancellationToken ct) =>
        LockedAsync(() => ApplyCoreAsync(subscription, config, revision, ct), ct);

    private async Task<SubscriptionResult> ApplyCoreAsync(Subscription subscription, string config, string revision, CancellationToken ct)
    {
        if (subscription.Fault.Length > 0)
        {
            return new(0, 0, 0, [], subscription.Fault);
        }
        var pending = await ConfigurationUpdates.ReadAsync(store, config, ct).ConfigureAwait(false);
        var current = await library.TextAsync(config, ct).ConfigureAwait(false);
        if (pending is null || pending.Revision != revision || current is null || ConfigurationRevision.Of(current) != pending.BaseRevision)
        {
            return new(0, 0, 0, [], "configuration-update-stale");
        }
        try
        {
            var fetched = await FetchAsync(subscription.Url, subscription.Pin, ct).ConfigureAwait(false);
            var members = await store.ListSubscriptionMembersAsync(subscription.Name, ct).ConfigureAwait(false);
            var names = await library.NamesAsync(ct).ConfigureAwait(false);
            var texts = await TextsAsync(members, names, ct).ConfigureAwait(false);
            var change = SubscriptionMerge.Plan(fetched.Configs, members, names, texts)
                .SingleOrDefault(one => one.Kind == SubscriptionChangeKind.Update && one.ConfigName == config);
            if (change is null || ConfigurationRevision.Of(change.ConfText) != revision)
            {
                await CheckCoreAsync(subscription, ct).ConfigureAwait(false);
                return new(0, 0, 0, [], "configuration-update-stale");
            }
            var latest = await library.TextAsync(config, ct).ConfigureAwait(false);
            if (latest is null || ConfigurationRevision.Of(latest) != pending.BaseRevision)
            {
                return new(0, 0, 0, [], "configuration-update-stale");
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
