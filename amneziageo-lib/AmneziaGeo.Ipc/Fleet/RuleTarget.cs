namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// Where a rule sends what it matches. A rule carries two of these: the tunnel it rides, and where it goes
/// while that one is not up.
/// </summary>
/// <param name="Mode">Which of the words below the field holds.</param>
/// <param name="Name">The server it names; empty unless the mode is <see cref="Server"/>.</param>
public sealed record RuleTarget(string Mode, string Name = "")
{
    /// <summary>
    /// The primary, or the first reserve the mode lists while no primary is up.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// The quickest to answer of the servers in the balancer.
    /// </summary>
    public const string Best = "best";

    /// <summary>
    /// The server the field names.
    /// </summary>
    public const string Server = "server";

    /// <summary>
    /// Nowhere: what the rule matches is dropped rather than let past the tunnel.
    /// </summary>
    public const string Block = "block";

    /// <summary>
    /// Past the tunnels: what the rule matches goes out as it is, on no server of the set.
    /// </summary>
    public const string Direct = "direct";

    /// <summary>
    /// What a field holds until it is addressed.
    /// </summary>
    public static readonly RuleTarget Default = new(Auto);

    /// <summary>
    /// Whether the field leaves the choice to the machine.
    /// </summary>
    public bool IsAuto => Mode == Auto;

    /// <summary>
    /// The word the field is stored by: a keyword, or the name it gives.
    /// </summary>
    public string Format()
    {
        return Mode == Server ? Name : Mode;
    }

    /// <summary>
    /// Reads one field. Anything but a keyword is a server name, so a server named after one loses the round trip.
    /// </summary>
    public static RuleTarget Parse(string? text)
    {
        var word = text?.Trim() ?? string.Empty;
        return word.ToLowerInvariant() switch
        {
            "" or Auto => Default,
            Best => new RuleTarget(Best),
            Block => new RuleTarget(Block),
            Direct => new RuleTarget(Direct),
            _ => new RuleTarget(Server, word),
        };
    }
}
