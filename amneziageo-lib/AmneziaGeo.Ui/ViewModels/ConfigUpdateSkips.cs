namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Ревизии подписок, обновление до которых главный экран пропустил.
/// </summary>
internal static class ConfigUpdateSkips
{
    /// <summary>
    /// Ждёт ли выбора обновление подписки до ревизии, которую назвал её сервер.
    /// </summary>
    public static bool Waits(string skipped, string subscription, string offered)
    {
        return offered.Length > 0 && !Lines(skipped).Contains(Line(subscription, offered));
    }

    /// <summary>
    /// Запись пропуска названных ревизий подписок.
    /// </summary>
    public static string Skip(IEnumerable<(string Subscription, string Offered)> revisions, string skipped = "")
    {
        var entries = revisions.Where(item => item.Offered.Length > 0).ToArray();
        var keys = entries.Select(item => item.Subscription + "\t").ToArray();
        return string.Join('\n', Lines(skipped)
            .Where(line => !keys.Any(key => line.StartsWith(key, StringComparison.Ordinal)))
            .Concat(entries.Select(item => Line(item.Subscription, item.Offered)))
            .Distinct(StringComparer.Ordinal));
    }

    private static HashSet<string> Lines(string skipped)
    {
        return skipped.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    private static string Line(string subscription, string offered)
    {
        return subscription + "\t" + offered;
    }
}
