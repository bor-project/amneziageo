using System.Diagnostics;
using System.Management;
using System.Net;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

internal sealed class LocalDohResources(ILogger logger) : ILocalDohResources
{
    public string? Template(IPAddress address)
    {
        logger.LogTrace("reading the local HTTPS resolver registration for {Address}", address);
        using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
            "SELECT ServerAddress, DohTemplate FROM MSFT_DNSClientDohServerAddress");
        searcher.Options.Timeout = TimeSpan.FromSeconds(6);
        using var records = searcher.Get();
        foreach (var item in records)
        {
            using var record = (ManagementBaseObject)item;
            if (record["ServerAddress"] is not string text || !IPAddress.TryParse(text, out var found))
            {
                throw new InvalidDataException("The DNS encryption provider returned an invalid server address.");
            }

            if (found.Equals(address))
            {
                return record["DohTemplate"] as string ?? string.Empty;
            }
        }

        return null;
    }

    public string? Binding(IPAddress address)
    {
        return LocalDohSslQuery.Read(address);
    }

    public void RemoveTemplate(IPAddress address)
    {
        Run($"dns delete encryption server={address}");
    }

    public void RemoveBinding(IPAddress address)
    {
        Run($"http delete sslcert ipport={new IPEndPoint(address, 443)}");
    }

    internal static string Run(string arguments)
    {
        using var process = Process.Start(ChildOutput.OfSystem(new ProcessStartInfo("netsh", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }));
        if (process is null)
        {
            throw new IOException("netsh did not start.");
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(6000))
        {
            process.Kill();
            throw new TimeoutException("netsh did not finish.");
        }

        Task.WhenAll(output, error).GetAwaiter().GetResult();
        var result = output.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new IOException($"netsh exited with code {process.ExitCode}: {(result + error.GetAwaiter().GetResult()).Trim()}");
        }

        return result;
    }
}
