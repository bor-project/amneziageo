using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Holds the user that owns the single machine-wide tunnel and resolves that user's store for the supervisor.
/// </summary>
internal sealed class ActiveTunnelScope(ScopedStoreFactory factory, ServiceManager serviceManager)
{
    private volatile string _ownerRoot = AppDataRoot.Base();
    private volatile string? _ownerSid;

    /// <summary>
    /// The data root of the tunnel owner.
    /// </summary>
    public string OwnerRoot => _ownerRoot;

    /// <summary>
    /// The SID of the tunnel owner, or null when unknown.
    /// </summary>
    public string? OwnerSid => _ownerSid;

    /// <summary>
    /// Binds the tunnel to a user's data root and SID.
    /// </summary>
    public void SetOwner(string root, string? sid)
    {
        _ownerRoot = root;
        _ownerSid = sid;
    }

    /// <summary>
    /// Returns whether the tunnel is owned by the given user (by SID when both are known, else by root).
    /// </summary>
    public bool IsOwnedBy(string root, string? sid)
    {
        if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(_ownerSid))
        {
            return string.Equals(_ownerSid, sid, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_ownerRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The owner's composite store.
    /// </summary>
    public IStateStore Store => factory.For(_ownerRoot);

    /// <summary>
    /// A config repository over the owner's store.
    /// </summary>
    public ConfigRepository ConfigRepo => new(Store, serviceManager);
}
