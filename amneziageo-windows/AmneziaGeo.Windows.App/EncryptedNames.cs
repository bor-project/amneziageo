using System.Net;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The list Windows keeps of resolvers it may ask over HTTPS, and this machine's own entry in it.
/// </summary>
internal static class EncryptedNames
{
    /// <summary>
    /// Tells Windows this resolver answers over HTTPS and to prefer that; tells whether the entry is there.
    /// </summary>
    public static bool Register(IPAddress address, string template, ILogger logger)
    {
        var added = Netsh($"dns add encryption server={address} dohtemplate={template} autoupgrade=yes udpfallback=yes", logger);
        var listed = Template(address, logger);
        if (string.Equals(listed, template, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Windows now asks {Address} over HTTPS by {Template}", address, template);
            return true;
        }

        logger.LogWarning("Windows did not take {Address} as a resolver it asks over HTTPS: {Answer}", address, (listed + added).Trim());
        return false;
    }

    /// <summary>
    /// Takes the entry back out.
    /// </summary>
    public static void Unregister(IPAddress address, ILogger logger)
    {
        Netsh($"dns delete encryption server={address}", logger);
    }

    public static string? Template(IPAddress address, ILogger logger)
    {
        return new LocalDohResources(logger).Template(address);
    }

    private static string Netsh(string arguments, ILogger logger)
    {
        try
        {
            return LocalDohResources.Run(arguments);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "netsh {Arguments} did not run", arguments);
            return string.Empty;
        }
    }
}
