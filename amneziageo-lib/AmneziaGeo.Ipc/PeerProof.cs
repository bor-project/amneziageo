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
    /// Returns the answer to a challenge from the private key of one side and the public key of the other.
    /// </summary>
    public static string Answer(string privateKey, string publicKey, string challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var shared = Curve25519.Product(Curve25519.Bytes(privateKey), Curve25519.Bytes(publicKey));

        return Convert.ToBase64String(HMACSHA256.HashData(shared, Encoding.UTF8.GetBytes(Context + challenge)));
    }
}
