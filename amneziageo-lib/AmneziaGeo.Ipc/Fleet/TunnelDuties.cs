namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// What one tunnel is on the hook for while several of them are up.
/// </summary>
/// <param name="CarriesDefault">Carries what no rule sends elsewhere.</param>
/// <param name="HoldsResolver">Holds this machine's name lookups.</param>
public sealed record TunnelDuties(bool CarriesDefault, bool HoldsResolver)
{
    /// <summary>
    /// What the only tunnel on a machine holds.
    /// </summary>
    public static readonly TunnelDuties Sole = new(true, true);

    /// <summary>
    /// What a tunnel out of the balancer holds: only what names it.
    /// </summary>
    public static readonly TunnelDuties None = new(false, false);

    /// <summary>
    /// Renders the duties for the tunnel process.
    /// </summary>
    public string Format()
    {
        var duties = new List<string>(2);
        if (CarriesDefault)
        {
            duties.Add(DefaultToken);
        }

        if (HoldsResolver)
        {
            duties.Add(ResolverToken);
        }

        return duties.Count > 0 ? string.Join(Separator, duties) : NoneToken;
    }

    /// <summary>
    /// Reads what the agent wrote. A tunnel nothing was written for is the only one there is, so it keeps every
    /// duty - that is what a machine running one tunnel has always done.
    /// </summary>
    public static TunnelDuties Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Sole;
        }

        var tokens = text.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new TunnelDuties(
            tokens.Contains(DefaultToken, StringComparer.OrdinalIgnoreCase),
            tokens.Contains(ResolverToken, StringComparer.OrdinalIgnoreCase));
    }

    private const char Separator = ',';
    private const string DefaultToken = "default";
    private const string ResolverToken = "resolver";
    private const string NoneToken = "none";
}
