using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Validates the TLS identity of a public proxy.
/// </summary>
internal static class ProxyTls
{
    internal static SslClientAuthenticationOptions Options(string name, string pin)
    {
        if (Uri.CheckHostName(name) == UriHostNameType.Unknown
            || pin.Length != 0 && (pin.Length != 64 || !pin.All(char.IsAsciiHexDigit)))
        {
            throw new ArgumentException("The TLS identity is invalid.");
        }

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = name,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        options.RemoteCertificateValidationCallback = (_, certificate, chain, errors) => Validate(certificate, chain, errors, pin, name);

        return options;
    }

    internal static bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, string pin, string name)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        var now = DateTime.UtcNow;
        if (!leaf.MatchesHostname(name, allowWildcards: true, allowCommonName: false)
            || now < leaf.NotBefore.ToUniversalTime() || now > leaf.NotAfter.ToUniversalTime()
            || chain?.ChainStatus.Any(status => (status.Status & ~(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0) == true)
        {
            return false;
        }

        var usage = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (usage is not null && !usage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0"))
        {
            return false;
        }

        return pin.Length == 0 ? (errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0
            : pin.Length == 64 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(leaf.RawData), Convert.FromHexString(pin));
    }
}
