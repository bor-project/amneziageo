namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// How the balancer of the set is looked at: how often, how long a silent server keeps what rides it, and how
/// much quicker another server has to answer to take that over.
/// </summary>
/// <param name="IntervalSeconds">How often the balancer is looked at again.</param>
/// <param name="Strikes">Silent looks in a row before the pick is handed over.</param>
/// <param name="MarginPercent">Share of the pick's round trip another server answers within to take it.</param>
public sealed record BalancePolicy(int IntervalSeconds = 30, int Strikes = 2, int MarginPercent = 50)
{
    /// <summary>
    /// What the balancer holds until the settings say otherwise.
    /// </summary>
    public static readonly BalancePolicy Default = new();
}
