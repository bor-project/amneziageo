namespace AmneziaGeo.Decl;

/// <summary>
/// A subscription the library is kept in step with: where it lives, what the panel reports about it and when
/// it was last read.
/// </summary>
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
    string LastError = "");

/// <summary>
/// A configuration a subscription brought in: the node it stands for and the name it took locally. Present is
/// false once the subscription stops offering that node.
/// </summary>
public sealed record SubscriptionMember(string Subscription, string Remark, string ConfigName, bool Present = true);
