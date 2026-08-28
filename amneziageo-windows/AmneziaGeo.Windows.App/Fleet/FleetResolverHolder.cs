namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// Держатель резольвера в наборе: лукапы машины идут через туннель, который везёт всё неадресованное. При
/// выключенном режиме - держатель под ним, до строки.
/// </summary>
internal sealed class FleetResolverHolder(AgentControl control, AgentMode mode, FleetControl fleet, FleetLive live)
    : ResolverHolder(control)
{
    /// <inheritdoc/>
    public override AgentControl? Current
    {
        get
        {
            if (!mode.MultiServer)
            {
                return base.Current;
            }

            var carrier = fleet.Carrier;
            return carrier is not null && live.Of(carrier) is { Running: true } holder ? holder : null;
        }
    }
}
