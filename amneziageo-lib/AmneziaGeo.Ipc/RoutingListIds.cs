using System.Globalization;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Reads the ids of routing lists sent as words of a command.
/// </summary>
public static class RoutingListIds
{
    /// <summary>
    /// Returns the ids in the order given, or null when a word is not an id.
    /// </summary>
    public static IReadOnlyList<long>? Parse(IReadOnlyList<string> words)
    {
        var ids = new List<long>(words.Count);
        foreach (var word in words)
        {
            if (!long.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return null;
            }

            ids.Add(id);
        }

        return ids;
    }
}
