using System.Collections.Concurrent;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Builds composite stores that pair the shared machine store with a per-user store.
/// </summary>
internal sealed class ScopedStoreFactory(IStateStore machine, UserStoreRegistry registry)
{
    private readonly ConcurrentDictionary<string, ScopedStateStore> _scopes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The shared machine store.
    /// </summary>
    public IStateStore Machine => machine;

    /// <summary>
    /// Returns the composite store for a user data root.
    /// </summary>
    public IStateStore For(string userRoot)
    {
        var key = Normalize(userRoot);
        return _scopes.GetOrAdd(key, root => new ScopedStateStore(machine, registry.GetOrOpen(root)));
    }

    private static string Normalize(string root)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}
