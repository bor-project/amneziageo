using System.Collections.Concurrent;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Opens and caches one per-user SQLite state store per data root.
/// </summary>
internal sealed class UserStoreRegistry
{
    private readonly ConcurrentDictionary<string, Lazy<SqliteStateStore>> _stores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the initialized state store for a user data root, opening it once.
    /// </summary>
    public IStateStore GetOrOpen(string userRoot)
    {
        var key = Normalize(userRoot);
        return _stores.GetOrAdd(key, root => new Lazy<SqliteStateStore>(() => Open(root))).Value;
    }

    /// <summary>
    /// Returns every already-opened user store.
    /// </summary>
    public IReadOnlyList<IStateStore> OpenedStores()
    {
        return _stores.Values.Where(entry => entry.IsValueCreated).Select(entry => (IStateStore)entry.Value).ToList();
    }

    /// <summary>
    /// Returns the normalized roots of every already-opened user store.
    /// </summary>
    public IReadOnlyList<string> OpenedRoots()
    {
        return _stores.Where(entry => entry.Value.IsValueCreated).Select(entry => entry.Key).ToList();
    }

    private static SqliteStateStore Open(string root)
    {
        Directory.CreateDirectory(root);
        var store = new SqliteStateStore(Path.Combine(root, "state.db"));
        store.InitializeAsync().GetAwaiter().GetResult();
        return store;
    }

    private static string Normalize(string root)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}
