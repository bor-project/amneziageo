using AmneziaGeo.Decl;

namespace AmneziaGeo.Geo;

/// <summary>
/// Что обновление подписки делает с одной конфигурацией.
/// </summary>
public enum SubscriptionChangeKind
{
    /// <summary>Узел появился в подписке впервые.</summary>
    Add,

    /// <summary>Узел уже заведён, у него сменился текст.</summary>
    Update,

    /// <summary>Узла в подписке больше нет.</summary>
    Gone,
}

/// <summary>
/// Одно решение по узлу подписки.
/// </summary>
public sealed record SubscriptionChange(string Remark, string ConfigName, string ConfText, SubscriptionChangeKind Kind);

/// <summary>
/// Сводит прочитанную подписку с тем, что уже заведено: чистая функция без базы и сети.
/// </summary>
public static class SubscriptionMerge
{
    /// <summary>
    /// Возвращает решения по каждому узлу подписки и по каждому пропавшему из неё.
    /// </summary>
    public static IReadOnlyList<SubscriptionChange> Plan(
        IReadOnlyList<VpnLinkCodec.Imported> fetched,
        IReadOnlyList<SubscriptionMember> members,
        IReadOnlyCollection<string> configNames)
    {
        var known = new Dictionary<string, SubscriptionMember>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            known[member.Remark] = member;
        }

        var taken = new HashSet<string>(configNames, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<SubscriptionChange>();

        foreach (var imported in fetched)
        {
            var remark = Remark(imported);
            if (!seen.Add(remark))
            {
                continue;
            }

            // Конфигурацию могли удалить руками - тогда узел заводится заново.
            if (known.TryGetValue(remark, out var member) && taken.Contains(member.ConfigName))
            {
                plan.Add(new SubscriptionChange(remark, member.ConfigName, imported.ConfText, SubscriptionChangeKind.Update));
                continue;
            }

            var name = Unique(imported.Name ?? VpnLinkCodec.HostName(imported.ConfText) ?? "config", taken);
            taken.Add(name);
            plan.Add(new SubscriptionChange(remark, name, imported.ConfText, SubscriptionChangeKind.Add));
        }

        foreach (var member in members)
        {
            if (!seen.Contains(member.Remark))
            {
                plan.Add(new SubscriptionChange(member.Remark, member.ConfigName, string.Empty, SubscriptionChangeKind.Gone));
            }
        }

        return plan;
    }

    // Узел опознаётся по своему имени, а без него - по пиру и хосту: адрес панели меняется, ключ пира нет.
    private static string Remark(VpnLinkCodec.Imported imported)
    {
        if (!string.IsNullOrWhiteSpace(imported.Name))
        {
            return imported.Name!.Trim();
        }

        return $"{PublicKey(imported.ConfText)}@{VpnLinkCodec.HostName(imported.ConfText) ?? string.Empty}";
    }

    private static string PublicKey(string confText)
    {
        foreach (var rawLine in confText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("PublicKey", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                return line[(equals + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    // Суффикс через дефис: имя конфигурации становится именем адаптера, а скобки и пробел он не принимает.
    private static string Unique(string baseName, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
