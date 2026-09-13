using System.Security.Cryptography;
using System.Text;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Proves to the server that this side holds the private key of its configuration, without the key leaving it.
/// </summary>
public static class PeerProof
{
    /// <summary>
    /// What the proof is bound to, so the shared secret answers for nothing else.
    /// </summary>
    public const string Context = "amneziageo-hello";

    /// <summary>
    /// What the countersign of the server is bound to.
    /// </summary>
    public const string ServerContext = "amneziageo-server";

    /// <summary>
    /// How many bytes the nonce of a client carries.
    /// </summary>
    public const int NonceBytes = 32;

    /// <summary>
    /// Returns the answer to a challenge from the private key of one side and the public key of the other.
    /// </summary>
    public static string Answer(string privateKey, string publicKey, string challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var shared = Curve25519.Product(Curve25519.Bytes(privateKey), Curve25519.Bytes(publicKey));

        return Convert.ToBase64String(HMACSHA256.HashData(shared, Encoding.UTF8.GetBytes(Context + challenge)));
    }

    /// <summary>
    /// Returns the countersign of an answer body from the private key of one side and the public key of the other.
    /// </summary>
    public static string Countersign(string privateKey, string publicKey, string nonce, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(nonce);

        var shared = Curve25519.Product(Curve25519.Bytes(privateKey), Curve25519.Bytes(publicKey));
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, shared);
        hmac.AppendData(Encoding.UTF8.GetBytes(ServerContext + nonce));
        hmac.AppendData(body);

        return Convert.ToBase64String(hmac.GetHashAndReset());
    }

    /// <summary>
    /// Tells whether a countersign is the one the keys, the nonce and the body produce.
    /// </summary>
    public static bool Countersigns(string privateKey, string publicKey, string nonce, ReadOnlySpan<byte> body, string? countersign)
    {
        if (countersign is not { Length: > 0 }
            || !Curve25519.IsKey(privateKey)
            || !Curve25519.IsKey(publicKey))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Countersign(privateKey, publicKey, nonce, body)),
            Encoding.UTF8.GetBytes(countersign));
    }
}
