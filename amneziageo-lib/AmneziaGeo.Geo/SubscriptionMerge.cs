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
/// Одно решение по узлу подписки. PreviousRemark несёт прежнее имя узла, когда подписка его переименовала.
/// </summary>
public sealed record SubscriptionChange(
    string Remark,
    string ConfigName,
    string ConfText,
    SubscriptionChangeKind Kind,
    string PreviousRemark = "");

/// <summary>
/// Сводит прочитанную подписку с тем, что уже заведено: чистая функция без базы и сети.
/// </summary>
public static class SubscriptionMerge
{
    /// <summary>
    /// Возвращает решения по каждому узлу подписки и по каждому пропавшему из неё. Тексты заведённых
    /// конфигураций опознают узел, которому подписка сменила имя.
    /// </summary>
    public static IReadOnlyList<SubscriptionChange> Plan(
        IReadOnlyList<VpnLinkCodec.Imported> fetched,
        IReadOnlyList<SubscriptionMember> members,
        IReadOnlyCollection<string> configNames,
        IReadOnlyDictionary<string, string>? texts = null)
    {
        var known = new Dictionary<string, SubscriptionMember>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            known[member.Remark] = member;
        }

        var taken = new HashSet<string>(configNames, StringComparer.Ordinal);
        var byKey = ByKey(members, texts);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<SubscriptionChange>();

        foreach (var imported in fetched)
        {
            var remark = Remark(imported);
            if (!seen.Add(remark))
            {
                continue;
            }

            var member = Match(remark, imported.ConfText, known, byKey, taken, kept);
            if (member is not null)
            {
                kept.Add(member.ConfigName);
                plan.Add(new SubscriptionChange(
                    remark, member.ConfigName, imported.ConfText, SubscriptionChangeKind.Update, member.Remark));
                continue;
            }

            var name = Unique(NodeName(imported, fetched), taken);
            taken.Add(name);
            plan.Add(new SubscriptionChange(remark, name, imported.ConfText, SubscriptionChangeKind.Add));
        }

        foreach (var member in members)
        {
            if (!kept.Contains(member.ConfigName) && !seen.Contains(member.Remark))
            {
                plan.Add(new SubscriptionChange(member.Remark, member.ConfigName, string.Empty, SubscriptionChangeKind.Gone));
            }
        }

        return plan;
    }

    // Узел ищется по имени, а переименованный - по ключу клиента: его подписка не меняет ни от порта, ни от
    // адреса. Занятая этим же чтением конфигурация второму узлу не достаётся.
    private static SubscriptionMember? Match(
        string remark,
        string confText,
        Dictionary<string, SubscriptionMember> known,
        Dictionary<string, SubscriptionMember> byKey,
        HashSet<string> taken,
        HashSet<string> kept)
    {
        // Конфигурацию могли удалить руками - тогда узел заводится заново.
        if (known.TryGetValue(remark, out var member) && Free(member))
        {
            return member;
        }

        var key = ClientKey(confText);
        return key.Length > 0 && byKey.TryGetValue(key, out var renamed) && Free(renamed) ? renamed : null;

        bool Free(SubscriptionMember item)
        {
            return taken.Contains(item.ConfigName) && !kept.Contains(item.ConfigName);
        }
    }

    private static Dictionary<string, SubscriptionMember> ByKey(
        IReadOnlyList<SubscriptionMember> members,
        IReadOnlyDictionary<string, string>? texts)
    {
        var map = new Dictionary<string, SubscriptionMember>(StringComparer.Ordinal);
        if (texts is null)
        {
            return map;
        }

        foreach (var member in members)
        {
            if (texts.TryGetValue(member.ConfigName, out var text) && ClientKey(text) is { Length: > 0 } key)
            {
                map.TryAdd(key, member);
            }
        }

        return map;
    }

    // Ключ клиента из [Interface]: он один на узел и переживает и переименование, и переезд сервера.
    private static string ClientKey(string confText)
    {
        foreach (var rawLine in confText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!line.StartsWith("PrivateKey", StringComparison.OrdinalIgnoreCase))
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

    // Хвост чужого имени именем не считается: панель именует только первый узел подписки.
    private static string NodeName(VpnLinkCodec.Imported imported, IReadOnlyList<VpnLinkCodec.Imported> fetched)
    {
        var name = imported.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return VpnLinkCodec.HostName(imported.ConfText) ?? "config";
        }

        if (!BareTag(name, fetched))
        {
            return name;
        }

        var port = VpnLinkCodec.EndpointPort(imported.ConfText);
        return port > 0 ? $"{name}-{port}" : name;
    }

    private static bool BareTag(string name, IReadOnlyList<VpnLinkCodec.Imported> fetched)
    {
        foreach (var other in fetched)
        {
            var full = other.Name?.Trim();
            if (full is { Length: > 0 } && full.Length > name.Length
                && full.EndsWith("-" + name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Узел опознаётся по ключу клиента: панель и переименовывает узлы, и раздаёт нескольким одно имя. Без
    // ключа остаётся имя, а без имени - пир и хост.
    private static string Remark(VpnLinkCodec.Imported imported)
    {
        if (ClientKey(imported.ConfText) is { Length: > 0 } key)
        {
            return "key:" + Digest(key);
        }

        if (!string.IsNullOrWhiteSpace(imported.Name))
        {
            return imported.Name!.Trim();
        }

        return $"{PublicKey(imported.ConfText)}@{VpnLinkCodec.HostName(imported.ConfText) ?? string.Empty}";
    }

    // Отпечаток ключа: в хранилище едет он, а не сам ключ.
    private static string Digest(string value)
    {
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..32];
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
