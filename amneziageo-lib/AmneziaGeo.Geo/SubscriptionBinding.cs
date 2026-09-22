using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Подписка, адрес которой назвал сервер конфигурации: конфигурация встаёт в её узлы, ревизия и сертификат сервера
/// ложатся на неё.
/// </summary>
public static class SubscriptionBinding
{
    /// <summary>
    /// Привязывает конфигурацию к подписке по адресу, который назвал её сервер, и запоминает его ревизию и
    /// сертификат. Подписку, заведённую пользователем на другой адрес, не трогает. Возвращает, изменилось ли что-то.
    /// </summary>
    public static async Task<bool> BindAsync(
        IStateStore store,
        string config,
        string? text,
        OfferedSubscription offered,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(offered);

        var remark = SubscriptionMerge.KeyRemark(text ?? string.Empty);
        if (remark.Length == 0)
        {
            return false;
        }

        var subscriptions = await store.ListSubscriptionsAsync(ct).ConfigureAwait(false);
        var member = (await store.ListSubscriptionMembersAsync(null, ct).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.ConfigName, config, StringComparison.Ordinal));
        if (member is not null)
        {
            var own = subscriptions.FirstOrDefault(item => string.Equals(item.Name, member.Subscription, StringComparison.Ordinal));
            if (own is null || (!own.FromHello && !string.Equals(own.Url, offered.Url, StringComparison.Ordinal)))
            {
                return false;
            }

            var marked = Marked(own, offered);
            if (marked == own)
            {
                return false;
            }

            await store.SaveSubscriptionAsync(marked, ct).ConfigureAwait(false);

            return true;
        }

        var found = subscriptions.FirstOrDefault(item => string.Equals(item.Url, offered.Url, StringComparison.Ordinal));
        var subscription = found is null
            ? new Subscription(
                Unique(offered.Url, subscriptions),
                offered.Url,
                CheckedAt: now,
                Revision: offered.Revision,
                Offered: offered.Revision,
                Pin: offered.Pin,
                FromHello: true)
            : Marked(found, offered);
        await store.SaveSubscriptionAsync(subscription, ct).ConfigureAwait(false);
        await store.SaveSubscriptionMemberAsync(new SubscriptionMember(subscription.Name, remark, config), ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Имена подписок, у которых сервер держит другую ревизию, чем прочитанная последней.
    /// </summary>
    public static async Task<IReadOnlySet<string>> StaleAsync(IStateStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        var subscriptions = await store.ListSubscriptionsAsync(ct).ConfigureAwait(false);

        return subscriptions.Where(item => item.Stale).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
    }

    // Отметки сервера на подписке; ещё не прочитанная подписка берёт ревизию сервера за свою.
    private static Subscription Marked(Subscription subscription, OfferedSubscription offered)
    {
        return subscription with
        {
            Url = subscription.FromHello ? offered.Url : subscription.Url,
            Revision = subscription.Revision.Length > 0 ? subscription.Revision : offered.Revision,
            Offered = offered.Revision,
            Pin = offered.Pin,
        };
    }

    // Имя по хосту адреса, с суффиксом через дефис, когда оно занято.
    private static string Unique(string url, IReadOnlyList<Subscription> subscriptions)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var address) ? address.Host : "subscription";
        var name = host;
        for (var i = 2; subscriptions.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal)); i++)
        {
            name = $"{host}-{i}";
        }

        return name;
    }
}
