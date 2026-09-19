using System.Diagnostics;
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
        Netsh($"dns delete encryption server={address}", logger);
        var added = Netsh($"dns add encryption server={address} dohtemplate={template} autoupgrade=yes udpfallback=yes", logger);
        var listed = Netsh($"dns show encryption server={address}", logger);
        if (listed.Contains(template, StringComparison.OrdinalIgnoreCase))
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

    private static string Netsh(string arguments, ILogger logger)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(6000);
            return output;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "netsh {Arguments} did not run", arguments);
            return string.Empty;
        }
    }
}
