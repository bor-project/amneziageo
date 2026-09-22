namespace AmneziaGeo.Decl;

/// <summary>
/// A subscription the library is kept in step with: where it lives, what the panel reports about it and when
/// it was last read.
/// </summary>
/// <param name="Revision">The mark of what the last reading brought, empty when the panel names none.</param>
/// <param name="Offered">The mark the server of a config named last, empty when it named none.</param>
/// <param name="Pin">The SHA-256 of the certificate the server named for the address, empty for none.</param>
/// <param name="FromHello">Whether the server of a config named the address, not the user.</param>
public sealed record Subscription(
    string Name,
    string Url,
    string Title = "",
    int IntervalHours = 0,
    long Upload = 0,
    long Download = 0,
    long Total = 0,
    DateTimeOffset? Expires = null,
    DateTimeOffset? CheckedAt = null,
    string LastError = "",
    string Revision = "",
    string Offered = "",
    string Pin = "",
    bool FromHello = false)
{
    /// <summary>
    /// Whether the server holds another revision than the one read last.
    /// </summary>
    public bool Stale => Revision.Length > 0 && Offered.Length > 0 && !string.Equals(Revision, Offered, StringComparison.Ordinal);
}

/// <summary>
/// A configuration a subscription brought in: the node it stands for and the name it took locally. Present is
/// false once the subscription stops offering that node.
/// </summary>
public sealed record SubscriptionMember(string Subscription, string Remark, string ConfigName, bool Present = true);
