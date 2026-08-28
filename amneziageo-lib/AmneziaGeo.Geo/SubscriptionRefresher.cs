using System.Net.Http;

using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Что подписка получила за одно чтение.
/// </summary>
public sealed record SubscriptionSnapshot(
    IReadOnlyList<VpnLinkCodec.Imported> Configs,
    string Title,
    int IntervalHours,
    SubscriptionCodec.Usage? Usage);

/// <summary>
/// Итог обновления одной подписки.
/// </summary>
public sealed record SubscriptionResult(int Added, int Updated, int Gone, IReadOnlyList<string> Rewritten, string Error = "")
{
    /// <summary>
    /// Прошло ли обновление без ошибки.
    /// </summary>
    public bool Ok => Error.Length == 0;
}

/// <summary>
/// Библиотека конфигураций со стороны подписки: у каждой платформы своё хранилище, набор операций один.
/// </summary>
public interface ISubscriptionLibrary
{
    /// <summary>
    /// Имена всех заведённых конфигураций.
    /// </summary>
    Task<IReadOnlyCollection<string>> NamesAsync(CancellationToken ct);

    /// <summary>
    /// Текст конфигурации, либо null, если её нет.
    /// </summary>
    Task<string?> TextAsync(string name, CancellationToken ct);

    /// <summary>
    /// Заводит конфигурацию.
    /// </summary>
    Task AddAsync(string name, string confText, CancellationToken ct);

    /// <summary>
    /// Переписывает текст заведённой конфигурации.
    /// </summary>
    Task EditAsync(string name, string confText, CancellationToken ct);

    /// <summary>
    /// Сносит конфигурацию со всем, что платформа держит рядом с ней.
    /// </summary>
    Task RemoveAsync(string name, CancellationToken ct);
}

/// <summary>
/// Читает подписку и приводит к ней библиотеку: новые узлы заводит, изменившиеся переписывает, пропавшие
/// помечает. Текст конфигурации меняется, имя и все настройки пользователя остаются на месте.
/// </summary>
public sealed class SubscriptionRefresher(GeoHttp http, IStateStore store, ISubscriptionLibrary library)
{
    /// <summary>
    /// Читает подписку по адресу и разбирает и тело, и заголовки.
    /// </summary>
    public async Task<SubscriptionSnapshot> FetchAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        using (request)
        {
            // На Accept: text/html панель отдаёт свою страницу вместо списка ссылок.
            request.Headers.TryAddWithoutValidation("Accept", "text/plain, */*");

            // Подписка везёт приватные ключи: чужой сертификат отвергаем, а не обходим.
            var response = await http.SendVerifiedAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            using (response)
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                return new SubscriptionSnapshot(
                    SubscriptionCodec.Parse(body),
                    SubscriptionCodec.ParseTitle(Header(response, "Profile-Title")) ?? string.Empty,
                    SubscriptionCodec.ParseUpdateInterval(Header(response, "Profile-Update-Interval")),
                    SubscriptionCodec.ParseUsage(Header(response, "Subscription-Userinfo")));
            }
        }
    }

    /// <summary>
    /// Обновляет одну подписку и запоминает, чем это кончилось.
    /// </summary>
    public async Task<SubscriptionResult> RefreshAsync(Subscription subscription, CancellationToken ct)
    {
        var snapshot = default(SubscriptionSnapshot);
        try
        {
            snapshot = await FetchAsync(subscription.Url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            await store.SaveSubscriptionAsync(
                subscription with { CheckedAt = DateTimeOffset.UtcNow, LastError = ex.Message },
                ct).ConfigureAwait(false);
            return new SubscriptionResult(0, 0, 0, [], ex.Message);
        }

        var members = await store.ListSubscriptionMembersAsync(subscription.Name, ct).ConfigureAwait(false);
        var names = await library.NamesAsync(ct).ConfigureAwait(false);
        var plan = SubscriptionMerge.Plan(snapshot.Configs, members, names);

        var added = 0;
        var gone = 0;
        var rewritten = new List<string>();
        foreach (var change in plan)
        {
            switch (change.Kind)
            {
                case SubscriptionChangeKind.Add:
                    await library.AddAsync(change.ConfigName, change.ConfText, ct).ConfigureAwait(false);
                    await store.SaveSubscriptionMemberAsync(
                        new SubscriptionMember(subscription.Name, change.Remark, change.ConfigName),
                        ct).ConfigureAwait(false);
                    added++;
                    break;
                case SubscriptionChangeKind.Update:
                    // Текст переписывается только когда он и правда другой: иначе перезапуск туннеля был бы зря.
                    var current = await library.TextAsync(change.ConfigName, ct).ConfigureAwait(false);
                    if (!string.Equals(current, change.ConfText, StringComparison.Ordinal))
                    {
                        await library.EditAsync(change.ConfigName, change.ConfText, ct).ConfigureAwait(false);
                        rewritten.Add(change.ConfigName);
                    }

                    await store.SaveSubscriptionMemberAsync(
                        new SubscriptionMember(subscription.Name, change.Remark, change.ConfigName),
                        ct).ConfigureAwait(false);
                    break;
                default:
                    await store.SaveSubscriptionMemberAsync(
                        new SubscriptionMember(subscription.Name, change.Remark, change.ConfigName, Present: false),
                        ct).ConfigureAwait(false);
                    gone++;
                    break;
            }
        }

        await store.SaveSubscriptionAsync(
            subscription with
            {
                Title = snapshot.Title.Length > 0 ? snapshot.Title : subscription.Title,
                IntervalHours = snapshot.IntervalHours > 0 ? snapshot.IntervalHours : subscription.IntervalHours,
                Upload = snapshot.Usage?.Upload ?? subscription.Upload,
                Download = snapshot.Usage?.Download ?? subscription.Download,
                Total = snapshot.Usage?.Total ?? subscription.Total,
                Expires = snapshot.Usage?.Expires ?? subscription.Expires,
                CheckedAt = DateTimeOffset.UtcNow,
                LastError = string.Empty,
            },
            ct).ConfigureAwait(false);

        return new SubscriptionResult(added, rewritten.Count, gone, rewritten);
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }
}
