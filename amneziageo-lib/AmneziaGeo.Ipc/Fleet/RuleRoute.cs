namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// Both ends of one rule: the tunnel it rides, and where it goes while that one is not up.
/// </summary>
/// <param name="Target">The tunnel it rides.</param>
/// <param name="Fallback">Where it goes while the target is not up.</param>
public sealed record RuleRoute(RuleTarget Target, RuleTarget Fallback)
{
    /// <summary>
    /// What a rule holds until it is addressed: the machine decides both ends.
    /// </summary>
    public static readonly RuleRoute Default = new(RuleTarget.Default, RuleTarget.Default);

    /// <summary>
    /// Whether both ends are left to the machine.
    /// </summary>
    public bool IsDefault => Target.IsAuto && Fallback.IsAuto;

    /// <summary>
    /// Both ends as they are stored.
    /// </summary>
    public string Format()
    {
        return $"{Target.Format()},{Fallback.Format()}";
    }

    /// <summary>
    /// Reads both ends; a value naming only one leaves the other to the machine.
    /// </summary>
    public static RuleRoute Parse(string? text)
    {
        var value = text ?? string.Empty;
        var comma = value.IndexOf(',');
        return comma < 0
            ? new RuleRoute(RuleTarget.Parse(value), RuleTarget.Default)
            : new RuleRoute(RuleTarget.Parse(value[..comma]), RuleTarget.Parse(value[(comma + 1)..]));
    }
}
