using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// A token a config proves its keys with: its public key, the clock, a nonce and the proof.
/// </summary>
/// <param name="Key">The public key of the config, in base64.</param>
/// <param name="Time">The clock, in seconds since 1970.</param>
/// <param name="Nonce">Random bytes, in base64.</param>
/// <param name="Proof">The proof of the keys, in base64.</param>
public sealed record ServiceProof(string Key, long Time, string Nonce, string Proof);

/// <summary>
/// Counts the token a config proves its keys to the services of its server with, and opens what they answer.
/// </summary>
public static class ServiceToken
{
    /// <summary>
    /// What the proof of a token is bound to.
    /// </summary>
    public const string Context = "amneziageo-hello";

    /// <summary>
    /// What the key of a sealed answer is bound to.
    /// </summary>
    public const string ReplyContext = "amneziageo-reply";

    /// <summary>
    /// What the key of a signal and of its answer is bound to.
    /// </summary>
    public const string SignalContext = "amneziageo-signal";

    /// <summary>
    /// The scheme a token travels under in the header of a websocket.
    /// </summary>
    public const string Scheme = "AmneziaGeo";

    /// <summary>
    /// How many bytes the nonce of a token carries.
    /// </summary>
    public const int NonceBytes = 16;

    /// <summary>
    /// How many bytes the nonce of the cipher carries.
    /// </summary>
    public const int IvBytes = 12;

    /// <summary>
    /// How many bytes the tag of a sealed answer carries.
    /// </summary>
    public const int TagBytes = 16;

    /// <summary>
    /// Returns a fresh token of a config for a moment of the clock.
    /// </summary>
    public static ServiceProof Make(string privateKey, string serverKey, long time)
    {
        var key = Curve25519.PublicOf(privateKey);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceBytes));
        var message = string.Create(CultureInfo.InvariantCulture, $"{Context}\n{key}\n{time}\n{nonce}");
        var proof = Convert.ToBase64String(HMACSHA256.HashData(Shared(privateKey, serverKey), Encoding.UTF8.GetBytes(message)));

        return new ServiceProof(key, time, nonce, proof);
    }

    /// <summary>
    /// Returns a token as the JSON a hello carries.
    /// </summary>
    public static string Body(ServiceProof token)
    {
        ArgumentNullException.ThrowIfNull(token);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("key", token.Key);
            writer.WriteNumber("time", token.Time);
            writer.WriteString("nonce", token.Nonce);
            writer.WriteString("proof", token.Proof);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Returns the header a websocket proves the keys of a config with.
    /// </summary>
    public static string Header(string privateKey, string serverKey, DateTimeOffset now)
    {
        var body = Encoding.UTF8.GetBytes(Body(Make(privateKey, serverKey, now.ToUnixTimeSeconds())));

        return $"{Scheme} {Convert.ToBase64String(body).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    /// <summary>
    /// Returns what counts a fresh header of the keys of a config text, or null when the text names no keys.
    /// </summary>
    public static Func<string>? HeaderOf(string? configText) =>
        ConfigServices.Target(configText) is { } target
            ? () => Header(target.PrivateKey, target.ServerKey, DateTimeOffset.UtcNow)
            : null;

    /// <summary>
    /// Opens an answer sealed for a token, or returns null when it was not sealed under its keys, its nonce and
    /// what the key is bound to.
    /// </summary>
    public static byte[]? Open(string privateKey, string serverKey, string nonce, string iv, string data, string context = ReplyContext)
    {
        try
        {
            var vector = Convert.FromBase64String(iv);
            var sealedBytes = Convert.FromBase64String(data);
            if (sealedBytes.Length < TagBytes)
            {
                return null;
            }

            var body = new byte[sealedBytes.Length - TagBytes];
            using var cipher = new AesGcm(SealKey(privateKey, serverKey, nonce, context), TagBytes);
            cipher.Decrypt(vector, sealedBytes.AsSpan(0, body.Length), sealedBytes.AsSpan(body.Length), body);

            return body;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Seals a body under the keys of a config, a nonce and what the key is bound to; returns the nonce of the
    /// cipher and the sealed bytes with the tag after them, in base64.
    /// </summary>
    public static (string Iv, string Data) Seal(string privateKey, string serverKey, string nonce, ReadOnlySpan<byte> body, string context)
    {
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var output = new byte[body.Length + TagBytes];
        using var cipher = new AesGcm(SealKey(privateKey, serverKey, nonce, context), TagBytes);
        cipher.Encrypt(iv, body, output.AsSpan(0, body.Length), output.AsSpan(body.Length));

        return (Convert.ToBase64String(iv), Convert.ToBase64String(output));
    }

    private static byte[] SealKey(string privateKey, string serverKey, string nonce, string context) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Shared(privateKey, serverKey), 32, Convert.FromBase64String(nonce), Encoding.UTF8.GetBytes(context));

    private static byte[] Shared(string privateKey, string serverKey) =>
        Curve25519.Product(Curve25519.Bytes(privateKey), Curve25519.Bytes(serverKey));
}
