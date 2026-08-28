using System.Text.Json;

using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// Ответ агента и что за ним стоит: счётчики и переписанные конфигурации.
/// </summary>
public sealed record SubscriptionOutcome(IpcAck Ack, int Added, int Updated, int Gone, IReadOnlyList<string> Rewritten, string Name = "")
{
    /// <summary>
    /// Ответ без единого изменения.
    /// </summary>
    public static SubscriptionOutcome Of(IpcAck ack)
    {
        return new SubscriptionOutcome(ack, 0, 0, 0, []);
    }
}

/// <summary>
/// Операции подписок, одни на все агенты: разбор аргументов, порядок действий и ответы платформа не
/// переписывает, у неё своя только библиотека конфигураций.
/// </summary>
public sealed class SubscriptionService(GeoHttp http, IStateStore store, ISubscriptionLibrary library)
{
    /// <summary>
    /// Заводит подписку по адресу и читает её сразу. Аргументы: адрес, необязательное имя.
    /// </summary>
    public async Task<SubscriptionOutcome> AddAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return SubscriptionOutcome.Of(new IpcAck(false, "add-subscription requires a url"));
        }

        var url = args[0].Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return SubscriptionOutcome.Of(new IpcAck(false, IpcMessage.Key("Agent_SubscriptionBadUrl", url)));
        }

        var name = args.Count > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1].Trim() : uri.Host;
        var subscriptions = await store.ListSubscriptionsAsync(ct).ConfigureAwait(false);
        if (subscriptions.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal)))
        {
            return SubscriptionOutcome.Of(new IpcAck(false, IpcMessage.Key("Agent_SubscriptionNameTaken", name)));
        }

        // Первое чтение проверяет адрес: подписка, которую не удалось прочитать, не заводится.
        var result = await Refresher().RefreshAsync(new Subscription(name, url), ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            await store.RemoveSubscriptionAsync(name, ct).ConfigureAwait(false);
            return SubscriptionOutcome.Of(new IpcAck(false, IpcMessage.Key("Agent_SubscriptionFailed", name, result.Error)));
        }

        return new SubscriptionOutcome(
            new IpcAck(true, IpcMessage.Key("Agent_SubscriptionAdded", name, result.Added)),
            result.Added,
            result.Updated,
            result.Gone,
            result.Rewritten,
            name);
    }

    /// <summary>
    /// Отдаёт подписки списком записей в JSON.
    /// </summary>
    public async Task<IpcAck> ListAsync(CancellationToken ct)
    {
        var subscriptions = await store.ListSubscriptionsAsync(ct).ConfigureAwait(false);
        var members = await store.ListSubscriptionMembersAsync(null, ct).ConfigureAwait(false);
        var entries = subscriptions.Select(item => new SubscriptionEntry(
            item.Name,
            item.Url,
            item.Title,
            item.IntervalHours,
            item.Upload,
            item.Download,
            item.Total,
            item.Expires?.ToUnixTimeSeconds() ?? 0,
            item.CheckedAt?.ToUnixTimeSeconds() ?? 0,
            item.LastError,
            members.Count(member => Belongs(member, item.Name) && member.Present),
            members.Count(member => Belongs(member, item.Name) && !member.Present)));

        return new IpcAck(true, JsonSerializer.Serialize(entries, IpcJson.Options));
    }

    /// <summary>
    /// Перечитывает названную подписку, а без имени - все. Переписанные конфигурации возвращаются
    /// вызвавшему: применить их к работающему туннелю - его дело.
    /// </summary>
    public async Task<SubscriptionOutcome> RefreshAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var subscriptions = await store.ListSubscriptionsAsync(ct).ConfigureAwait(false);
        var wanted = args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? subscriptions.Where(item => string.Equals(item.Name, args[0].Trim(), StringComparison.Ordinal)).ToList()
            : [.. subscriptions];
        if (wanted.Count == 0)
        {
            return SubscriptionOutcome.Of(new IpcAck(false, IpcMessage.Key("Agent_SubscriptionUnknown", args.Count > 0 ? args[0] : string.Empty)));
        }

        var refresher = Refresher();
        var added = 0;
        var updated = 0;
        var gone = 0;
        var error = string.Empty;
        var rewritten = new List<string>();
        foreach (var subscription in wanted)
        {
            var result = await refresher.RefreshAsync(subscription, ct).ConfigureAwait(false);
            added += result.Added;
            updated += result.Updated;
            gone += result.Gone;
            rewritten.AddRange(result.Rewritten);
            if (!result.Ok && error.Length == 0)
            {
                error = result.Error;
            }
        }

        var ack = error.Length > 0
            ? new IpcAck(false, error)
            : new IpcAck(true, IpcMessage.Key("Agent_SubscriptionRefreshed", added, updated, gone));

        return new SubscriptionOutcome(ack, added, updated, gone, rewritten);
    }

    /// <summary>
    /// Снимает подписку, а со вторым аргументом "configs" - и приведённые ею конфигурации. Имя
    /// работающей конфигурации отбивает снос вместе с ней.
    /// </summary>
    public async Task<IpcAck> RemoveAsync(IReadOnlyList<string> args, string? running, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "remove-subscription requires a name");
        }

        var name = args[0].Trim();
        var subscriptions = await store.ListSubscriptionsAsync(ct).ConfigureAwait(false);
        if (!subscriptions.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal)))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_SubscriptionUnknown", name));
        }

        var members = await store.ListSubscriptionMembersAsync(name, ct).ConfigureAwait(false);
        var withConfigs = args.Count > 1 && string.Equals(args[1], "configs", StringComparison.OrdinalIgnoreCase);
        if (withConfigs && running is { Length: > 0 } && members.Any(member => string.Equals(member.ConfigName, running, StringComparison.Ordinal)))
        {
            return new IpcAck(false, $"config {running} is running; disconnect first");
        }

        await store.RemoveSubscriptionAsync(name, ct).ConfigureAwait(false);
        if (withConfigs)
        {
            var names = await library.NamesAsync(ct).ConfigureAwait(false);
            foreach (var member in members)
            {
                if (names.Contains(member.ConfigName, StringComparer.Ordinal))
                {
                    await library.RemoveAsync(member.ConfigName, ct).ConfigureAwait(false);
                }
            }
        }

        return new IpcAck(true, IpcMessage.Key("Agent_SubscriptionRemoved", name));
    }

    /// <summary>
    /// Адрес подписки, из которой пришла конфигурация; пустая строка, если она пришла не оттуда.
    /// </summary>
    public async Task<IpcAck> ConfigUrlAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return new IpcAck(false, "config-subscription requires a name");
        }

        var name = args[0].Trim();
        var member = (await store.ListSubscriptionMembersAsync(null, ct).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.ConfigName, name, StringComparison.Ordinal));
        if (member is null)
        {
            return new IpcAck(true, string.Empty);
        }

        var subscription = (await store.ListSubscriptionsAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.Name, member.Subscription, StringComparison.Ordinal));
        return new IpcAck(true, subscription?.Url ?? string.Empty);
    }

    /// <summary>
    /// Конфигурации, которые несёт подписка и которые она всё ещё несёт.
    /// </summary>
    public async Task<IReadOnlyList<string>> MembersAsync(string subscription, CancellationToken ct)
    {
        var members = await store.ListSubscriptionMembersAsync(subscription, ct).ConfigureAwait(false);
        return [.. members.Where(member => member.Present).Select(member => member.ConfigName)];
    }

    /// <summary>
    /// Подписки, которым по их интервалу пора обновиться.
    /// </summary>
    public async Task<IReadOnlyList<Subscription>> DueAsync(int fallbackHours, DateTimeOffset now, CancellationToken ct)
    {
        var subscriptions = await store.ListSubscriptionsAsync(ct).ConfigureAwait(false);
        return [.. subscriptions.Where(item => Due(item, fallbackHours, now))];
    }

    /// <summary>
    /// Пора ли перечитывать подписку: свой интервал сервера, а без него - заданный настройкой.
    /// </summary>
    public static bool Due(Subscription subscription, int fallbackHours, DateTimeOffset now)
    {
        if (subscription.CheckedAt is not { } checkedAt)
        {
            return true;
        }

        var hours = subscription.IntervalHours > 0 ? subscription.IntervalHours : fallbackHours;
        return hours > 0 && now - checkedAt >= TimeSpan.FromHours(hours);
    }

    private static bool Belongs(SubscriptionMember member, string subscription)
    {
        return string.Equals(member.Subscription, subscription, StringComparison.Ordinal);
    }

    private SubscriptionRefresher Refresher()
    {
        return new SubscriptionRefresher(http, store, library);
    }
}
