namespace AmneziaGeo.Decl;

/// <summary>
/// Снимает удалённую конфигурацию с подписки, которая её принесла.
/// </summary>
public static class ConfigForget
{
    /// <summary>
    /// Убирает конфигурацию из узлов её подписки, а подписку, у которой узлов не осталось, снимает целиком.
    /// </summary>
    public static async Task CarryAsync(IStateStore store, string name, CancellationToken ct = default)
    {
        var members = await store.ListSubscriptionMembersAsync(null, ct).ConfigureAwait(false);
        var own = members.Where(member => string.Equals(member.ConfigName, name, StringComparison.Ordinal)).ToList();
        foreach (var member in own)
        {
            await store.RemoveSubscriptionMemberAsync(member.Subscription, member.Remark, ct).ConfigureAwait(false);
        }

        foreach (var subscription in own.Select(member => member.Subscription).Distinct(StringComparer.Ordinal))
        {
            var kept = members.Any(member => string.Equals(member.Subscription, subscription, StringComparison.Ordinal)
                && !string.Equals(member.ConfigName, name, StringComparison.Ordinal));
            if (!kept)
            {
                await store.RemoveSubscriptionAsync(subscription, ct).ConfigureAwait(false);
            }
        }
    }
}
