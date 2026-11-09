using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AmneziaGeo.Ipc;

internal sealed class HttpGetAuthentication
{
    internal const string Version = "2";
    private readonly byte[] _identity;
    private readonly byte[] _request;
    private readonly byte[] _response;
    private readonly byte[] _challenge;
    internal string KeyId => Convert.ToHexStringLower(_identity);

    /// <summary>
    /// ctor
    /// </summary>
    internal HttpGetAuthentication(string credential)
    {
        if (!Guid.TryParse(credential, out var id) || id == Guid.Empty)
        {
            throw new InvalidDataException("The HTTP GET key is invalid.");
        }

        var secret = Encoding.UTF8.GetBytes(id.ToString("D"));
        _identity = SHA256.HashData(Encoding.UTF8.GetBytes("amneziageo.http-get.id.v2\0" + id.ToString("D")));
        var salt = "amneziageo.http-get.v2"u8.ToArray();
        _request = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "request"u8.ToArray());
        _response = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "response"u8.ToArray());
        _challenge = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "challenge"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
    }

    internal void VerifyChallenge(string path, byte[] nonce, byte[] epoch, ulong time, string signature)
    {
        using var data = new MemoryStream();
        Put(data, "amneziageo.http-get.challenge.v2");
        Put(data, path);
        Put(data, _identity);
        Put(data, nonce);
        Put(data, epoch);
        Number(data, time);
        Check(_challenge, data.ToArray(), signature);
    }

    internal (byte[] Digest, string Signature) SignRequest(string path, IReadOnlyDictionary<string, string> query,
        byte[] payload, byte[] epoch, ulong time, byte[] nonce, string method = "GET")
    {
        if (method is not ("GET" or "POST") || epoch.Length != 16 || nonce.Length != 16 || path.Length is 0 or > 1024 || path[0] != '/' || query.Count > 32)
        {
            throw new InvalidDataException("The HTTP GET signature fields are invalid.");
        }

        using var data = new MemoryStream();
        Put(data, "amneziageo.http-get.request.v2");
        Put(data, method);
        Put(data, path);
        Put(data, _identity);
        Put(data, epoch);
        Number(data, time);
        Put(data, nonce);
        Short(data, checked((ushort)query.Count));
        foreach (var pair in query.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key.Length is 0 or > 64 || pair.Value.Length > 1024)
            {
                throw new InvalidDataException("The HTTP GET query is invalid.");
            }

            Put(data, pair.Key);
            Put(data, pair.Value);
        }

        Put(data, SHA256.HashData(payload));
        var message = data.ToArray();
        return (SHA256.HashData(message), Convert.ToBase64String(HMACSHA256.HashData(_request, message)));
    }

    internal void VerifyResponse(byte[] request, int status, byte[] epoch, ulong time, string version,
        string sequence, string dropped, string fault, byte[] body, string signature)
    {
        if (request.Length != 32 || status is < 100 or > 599 || epoch.Length != 16
            || sequence.Length > 20 || dropped.Length > 20 || fault.Length > 64)
        {
            throw new InvalidDataException("The HTTP GET response fields are invalid.");
        }

        using var data = new MemoryStream();
        Put(data, "amneziageo.http-get.response.v2");
        Put(data, request);
        Short(data, checked((ushort)status));
        Put(data, epoch);
        Number(data, time);
        Put(data, version);
        Put(data, sequence);
        Put(data, dropped);
        Put(data, fault);
        Put(data, SHA256.HashData(body));
        Check(_response, data.ToArray(), signature);
    }

    private static void Check(byte[] key, byte[] message, string signature)
    {
        var decoded = new byte[32];
        if (!Convert.TryFromBase64String(signature, decoded, out var count) || count != 32
            || Convert.ToBase64String(decoded) != signature
            || !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, message), decoded))
        {
            throw new InvalidDataException("The HTTP GET signature is invalid.");
        }
    }

    private static void Put(Stream data, string text) => Put(data, Encoding.UTF8.GetBytes(text));

    private static void Put(Stream data, byte[] value)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        data.Write(length);
        data.Write(value);
    }

    private static void Number(Stream data, ulong number)
    {
        var value = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(value, number);
        data.Write(value);
    }

    private static void Short(Stream data, ushort number)
    {
        var value = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(value, number);
        data.Write(value);
    }
}
