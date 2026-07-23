using System.Collections.Generic;
using System.Linq;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Уникализация имени: базовое, затем "&lt;base&gt; 2", "&lt;base&gt; 3"…
/// </summary>
internal static class UniqueName
{
    public static string Resolve(string baseName, IEnumerable<string> taken)
    {
        var existing = taken.ToHashSet(System.StringComparer.Ordinal);
        var trimmed = string.IsNullOrWhiteSpace(baseName) ? baseName : baseName.Trim();
        if (!existing.Contains(trimmed))
        {
            return trimmed;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{trimmed} {i}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}