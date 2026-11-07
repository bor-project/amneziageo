using System.Security.Cryptography;
using System.Text;

namespace AmneziaGeo.Decl;

/// <summary>
/// Certifies the publishing WG interface of a catalog.
/// </summary>
public sealed record OfferedProxySourceProof(string PublicKey, string Issuer, string Signature);

/// <summary>
/// Verifies catalog identities.
/// </summary>
public static class ProxySourceIdentity
{
    /// <summary>
    /// Verifies the source binding of an authenticated issuer.
    /// </summary>
    public static bool Verify(OfferedProxyCatalog catalog, string issuer, out string publicKey)
    {
        publicKey = "";
        if (catalog.Proof is not { } proof || !Guid.TryParse(catalog.Source, out var source) || source == Guid.Empty
            || proof.PublicKey is not { Length: > 0 and <= 512 } || proof.Signature is not { Length: > 0 and <= 128 }
            || string.IsNullOrEmpty(issuer) || !string.Equals(proof.Issuer, issuer, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var issuerBytes = Convert.FromBase64String(issuer);
            if (issuerBytes.Length != 32 || Convert.ToBase64String(issuerBytes) != issuer)
            {
                return false;
            }

            var key = Convert.FromBase64String(proof.PublicKey);
            var signature = Convert.FromBase64String(proof.Signature);
            if (signature.Length != 64)
            {
                return false;
            }

            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(key, out var consumed);
            if (consumed != key.Length || verifier.KeySize != 256
                || verifier.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                return false;
            }

            var message = Encoding.UTF8.GetBytes("amneziageo.proxy-source.v1\n" + source.ToString("D") + "\n" + issuer);
            if (!verifier.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return false;
            }

            publicKey = Convert.ToBase64String(verifier.ExportSubjectPublicKeyInfo());
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Names a signed source in the client catalog.
    /// </summary>
    public static string Signed(string source) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("amneziageo.proxy-signed.v1\n" + Guid.Parse(source).ToString("D"))).AsSpan(0, 16), bigEndian: true).ToString("D");

    /// <summary>
    /// Names an unsigned source within its verified WG issuer.
    /// </summary>
    public static string Unsigned(string source, string issuer) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("amneziageo.proxy-unsigned.v1\n" + source + "\n" + issuer)).AsSpan(0, 16), bigEndian: true).ToString("D");

    /// <summary>
    /// Names a legacy WebSocket source.
    /// </summary>
    public static string Legacy(string issuer) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("amneziageo.proxy-legacy.v1\n" + issuer)).AsSpan(0, 16), bigEndian: true).ToString("D");
}
