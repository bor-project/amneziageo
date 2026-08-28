using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Geo;

/// <summary>
/// Ошибка формата wg-quick / AmneziaWG конфигурации.
/// </summary>
public sealed class WgConfigFormatException : Exception
{
    /// <summary>
    /// ctor
    /// </summary>
    public WgConfigFormatException(string why, string offender, bool unknownKey = false)
        : base($"{why}: \"{offender}\"")
    {
        Offender = offender;
        UnknownKey = unknownKey;
    }

    /// <summary>
    /// Ключ или строка, на которой разбор остановился.
    /// </summary>
    public string Offender { get; }

    /// <summary>
    /// Ключ неизвестен этой версии клиента.
    /// </summary>
    public bool UnknownKey { get; }
}

/// <summary>
/// Проверяет текст wg-quick / AmneziaWG теми же правилами, что и нативный движок (conf/parser.go), чтобы конфиг,
/// который движок отверг бы при подъёме туннеля, отбивался уже при импорте.
/// </summary>
public static class WgConfigValidator
{
    private enum Section
    {
        None,
        Interface,
        Peer,
    }

    // Теги, для которых движок допускает пустое значение.
    private static readonly HashSet<string> SpecialHandshakeTags = new(StringComparer.Ordinal)
    {
        "i1", "i2", "i3", "i4", "i5", "j1", "j2", "j3", "itime", "headerprotectionkey",
    };

    /// <summary>
    /// Проверяет конфиг и бросает <see cref="WgConfigFormatException"/> при первой ошибке.
    /// </summary>
    public static void Validate(string text)
    {
        var section = Section.None;
        var sawPrivateKey = false;
        var peerPublicKeys = new List<bool>();
        var currentPeerHasPublicKey = false;
        var inPeer = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var pound = line.IndexOf('#');
            if (pound >= 0)
            {
                line = line[..pound];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var lower = line.ToLowerInvariant();
            if (lower == "[interface]")
            {
                if (inPeer)
                {
                    peerPublicKeys.Add(currentPeerHasPublicKey);
                    inPeer = false;
                }

                section = Section.Interface;
                continue;
            }

            if (lower == "[peer]")
            {
                if (inPeer)
                {
                    peerPublicKeys.Add(currentPeerHasPublicKey);
                }

                inPeer = true;
                currentPeerHasPublicKey = false;
                section = Section.Peer;
                continue;
            }

            if (section == Section.None)
            {
                throw new WgConfigFormatException("Line must occur in a section", line);
            }

            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                throw new WgConfigFormatException("Config key is missing an equals separator", line);
            }

            var key = lower[..equals].Trim();
            var val = line[(equals + 1)..].Trim();
            if (val.Length == 0 && !SpecialHandshakeTags.Contains(key))
            {
                throw new WgConfigFormatException("Key must have a value", line);
            }

            if (section == Section.Interface)
            {
                ValidateInterfaceEntry(key, val);
                if (key == "privatekey")
                {
                    sawPrivateKey = true;
                }
            }
            else
            {
                ValidatePeerEntry(key, val);
                if (key == "publickey")
                {
                    currentPeerHasPublicKey = true;
                }
            }
        }

        if (inPeer)
        {
            peerPublicKeys.Add(currentPeerHasPublicKey);
        }

        if (!sawPrivateKey)
        {
            throw new WgConfigFormatException("An interface must have a private key", "[none specified]");
        }

        foreach (var hasPublicKey in peerPublicKeys)
        {
            if (!hasPublicKey)
            {
                throw new WgConfigFormatException("All peers must have public keys", "[none specified]");
            }
        }
    }

    private static void ValidateInterfaceEntry(string key, string val)
    {
        switch (key)
        {
            case "privatekey":
                ParseKeyBase64(val);
                break;
            case "listenport":
                ParsePort(val);
                break;
            case "jc":
            case "jmin":
            case "jmax":
            case "s1":
            case "s2":
            case "s3":
            case "s4":
                ParseUint16(val, key);
                break;
            case "headerprotectionkey":
                if (val.Length > 0)
                {
                    ParseKeyBase64(val);
                }

                break;
            case "contentpaddingaddition":
            case "rekeyaftertime":
            case "rekeytimeout":
            case "rejectaftertime":
            case "keepalivetimeout":
            case "maxhandshakeattempts":
            case "h1":
            case "h2":
            case "h3":
            case "h4":
                ParseUintRange(val, key);
                break;
            case "randomtrailers":
            case "disablecookies":
                ParseAwgBool(val, key);
                break;
            case "i1":
            case "i2":
            case "i3":
            case "i4":
            case "i5":
            case "preup":
            case "postup":
            case "predown":
            case "postdown":
                break;
            case "mtu":
                ParseMtu(val);
                break;
            case "address":
                foreach (var entry in SplitList(val))
                {
                    ParseIpCidr(entry);
                }

                break;
            case "dns":
                SplitList(val);
                break;
            case "table":
                ParseTableOff(val);
                break;
            default:
                throw new WgConfigFormatException("Invalid key for [Interface] section", key, unknownKey: true);
        }
    }

    private static void ValidatePeerEntry(string key, string val)
    {
        switch (key)
        {
            case "publickey":
            case "presharedkey":
                ParseKeyBase64(val);
                break;
            case "allowedips":
                foreach (var entry in SplitList(val))
                {
                    ParseIpCidr(entry);
                }

                break;
            case "persistentkeepalive":
                ParsePersistentKeepalive(val);
                break;
            case "endpoint":
                ParseEndpoint(val);
                break;
            default:
                throw new WgConfigFormatException("Invalid key for [Peer] section", key, unknownKey: true);
        }
    }

    private static void ParseKeyBase64(string s)
    {
        var bytes = TryDecodeBase64(s);
        if (bytes is null)
        {
            throw new WgConfigFormatException("Invalid key", s);
        }

        if (bytes.Length != 32)
        {
            throw new WgConfigFormatException("Keys must decode to exactly 32 bytes", s);
        }
    }

    private static byte[]? TryDecodeBase64(string s)
    {
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void ParsePort(string s)
    {
        if (!int.TryParse(s, out var port) || port < 0 || port > 65535)
        {
            throw new WgConfigFormatException("Invalid port", s);
        }
    }

    private static void ParseUint16(string s, string name)
    {
        if (!int.TryParse(s, out var value) || value < 0 || value > 65535)
        {
            throw new WgConfigFormatException($"Invalid {name}", s);
        }
    }

    // Диапазон движка: одно число либо "низ-верх".
    private static void ParseUintRange(string s, string name)
    {
        var parts = s.Split('-');
        if (parts.Length > 2)
        {
            throw new WgConfigFormatException($"Invalid {name}", s);
        }

        if (!uint.TryParse(parts[0], out var lo))
        {
            throw new WgConfigFormatException($"Invalid {name}", s);
        }

        if (parts.Length == 2 && (!uint.TryParse(parts[1], out var hi) || hi < lo))
        {
            throw new WgConfigFormatException($"Invalid {name}", s);
        }
    }

    // Флаг 3.1: движку уходит 1/0, поэтому опечатка молча выключила бы защиту.
    private static void ParseAwgBool(string s, string name)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "on":
            case "off":
            case "1":
            case "0":
            case "true":
            case "false":
            case "t":
            case "f":
            case "yes":
            case "no":
                return;
            default:
                throw new WgConfigFormatException($"Invalid {name}", s);
        }
    }

    private static void ParseMtu(string s)
    {
        if (!int.TryParse(s, out var mtu) || mtu < 576 || mtu > 65535)
        {
            throw new WgConfigFormatException("Invalid MTU", s);
        }
    }

    // В 3.1 это тоже диапазон, а не одно число.
    private static void ParsePersistentKeepalive(string s)
    {
        if (string.Equals(s, "off", StringComparison.Ordinal) || string.Equals(s, "(off)", StringComparison.Ordinal))
        {
            return;
        }

        ParseUintRange(s, "persistent keepalive");
    }

    private static void ParseTableOff(string s)
    {
        if (string.Equals(s, "off", StringComparison.Ordinal)
            || string.Equals(s, "auto", StringComparison.Ordinal)
            || string.Equals(s, "main", StringComparison.Ordinal))
        {
            return;
        }

        if (!uint.TryParse(s, out _))
        {
            throw new WgConfigFormatException("Invalid table", s);
        }
    }

    private static void ParseIpCidr(string s)
    {
        var slash = s.IndexOf('/');
        var addrStr = slash < 0 ? s : s[..slash];
        var cidrStr = slash < 0 ? string.Empty : s[(slash + 1)..];

        if (!IPAddress.TryParse(addrStr, out var addr))
        {
            throw new WgConfigFormatException("Invalid IP address", s);
        }

        if (cidrStr.Length > 0)
        {
            var isV4 = addr.AddressFamily == AddressFamily.InterNetwork;
            if (!int.TryParse(cidrStr, out var cidr) || cidr < 0 || cidr > 128 || (isV4 && cidr > 32))
            {
                throw new WgConfigFormatException("Invalid network prefix length", s);
            }
        }
    }

    private static void ParseEndpoint(string s)
    {
        var i = s.LastIndexOf(':');
        if (i < 0)
        {
            throw new WgConfigFormatException("Missing port from endpoint", s);
        }

        var host = s[..i];
        var portStr = s[(i + 1)..];
        if (host.Length < 1)
        {
            throw new WgConfigFormatException("Invalid endpoint host", host);
        }

        ParsePort(portStr);

        var hostColon = host.IndexOf(':');
        if (host[0] == '[' || host[^1] == ']' || hostColon > 0)
        {
            var bracketed = host.Length > 3 && host[0] == '[' && host[^1] == ']' && hostColon > 0;
            if (!bracketed)
            {
                throw new WgConfigFormatException("Brackets must contain an IPv6 address", host);
            }

            var end = host.Length - 1;
            var percent = host.LastIndexOf('%');
            if (percent > 1)
            {
                end = percent;
            }

            if (!IPAddress.TryParse(host[1..end], out _))
            {
                throw new WgConfigFormatException("Brackets must contain an IPv6 address", host);
            }
        }
    }

    private static IReadOnlyList<string> SplitList(string s)
    {
        var parts = s.Split(',');
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                throw new WgConfigFormatException("Two commas in a row", s);
            }

            result.Add(trimmed);
        }

        return result;
    }
}
