using AmneziaGeo.Windows.App.Fleet;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The sources of the name subsystem, whose rows make up the resolver log.
/// </summary>
internal static class ResolverLog
{
    private static readonly HashSet<string> _sources = new(StringComparer.Ordinal)
    {
        nameof(DnsProxy),
        nameof(DomainTracker),
        nameof(DnsHealthService),
        nameof(DnsAnswerLearner),
        nameof(DnsConfigurator),
        nameof(AppDnsTracker),
        nameof(LocalDohGuard),
        nameof(FleetLentNames),
    };

    /// <summary>
    /// The sources the resolver log takes.
    /// </summary>
    public static IReadOnlyCollection<string> Sources => _sources;

    /// <summary>
    /// Whether rows of this source go to the resolver log.
    /// </summary>
    public static bool Takes(string source)
    {
        return _sources.Contains(source);
    }
}
