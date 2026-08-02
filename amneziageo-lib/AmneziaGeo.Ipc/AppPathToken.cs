namespace AmneziaGeo.Ipc;

/// <summary>
/// Rewrites app-rule file paths to portable %ENV% tokens so a routing rule survives export to
/// another machine or user account, and matches back regardless of who is running the app.
/// </summary>
public static class AppPathToken
{
    private const string LocalAppData = "AppData\\Local\\";
    private const string RoamingAppData = "AppData\\Roaming\\";

    /// <summary>
    /// Replaces a leading known-folder prefix of an absolute path with its %ENV% token. Idempotent;
    /// a no-op for an already-tokenized path or one under no known folder.
    /// </summary>
    public static string Tokenize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var p = path.Trim().Trim('"');
        if (p.StartsWith('%'))
        {
            return p;
        }

        // Per-user folders are resolved by path shape, not Environment expansion: the tunnel service
        // runs as LocalSystem, where %LOCALAPPDATA% would resolve to the system profile.
        var usersRoot = UsersRoot();
        if (usersRoot.Length > 0 && p.StartsWith(usersRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            var afterRoot = p[(usersRoot.Length + 1)..];
            var slash = afterRoot.IndexOf('\\');
            if (slash > 0)
            {
                var rest = afterRoot[(slash + 1)..];
                if (rest.StartsWith(LocalAppData, StringComparison.OrdinalIgnoreCase))
                {
                    return "%LOCALAPPDATA%\\" + rest[LocalAppData.Length..];
                }

                if (rest.StartsWith(RoamingAppData, StringComparison.OrdinalIgnoreCase))
                {
                    return "%APPDATA%\\" + rest[RoamingAppData.Length..];
                }

                return "%USERPROFILE%\\" + rest;
            }
        }

        foreach (var (dir, token) in MachineFolders())
        {
            if (dir.Length > 0 && p.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return token + "\\" + p[(dir.Length + 1)..];
            }
        }

        return p;
    }

    /// <summary>
    /// Returns the parent of a versioned leaf folder (Squirrel app-x.y.z and similar), so an app:dir= rule
    /// survives the app auto-updating into a new version folder. A non-versioned leaf passes through unchanged.
    /// </summary>
    public static string StripVersionedLeaf(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return dir;
        }

        var trimmed = dir.TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        if (slash <= 0)
        {
            return trimmed;
        }

        var leaf = trimmed[(slash + 1)..];
        return LooksVersioned(leaf) ? trimmed[..slash] : trimmed;
    }

    // A folder name that carries a version: Squirrel's app-<ver>, a v-prefixed semver, or a bare dotted number.
    private static bool LooksVersioned(string segment)
    {
        if (segment.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
            && segment.Length > 4
            && char.IsDigit(segment[4]))
        {
            return true;
        }

        var i = 0;
        if (segment.Length > 0 && (segment[0] == 'v' || segment[0] == 'V'))
        {
            i = 1;
        }

        if (i >= segment.Length || !char.IsDigit(segment[i]))
        {
            return false;
        }

        var hasDot = false;
        for (; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '.')
            {
                hasDot = true;
            }
            else if (!char.IsDigit(c))
            {
                break;
            }
        }

        return hasDot;
    }

    /// <summary>
    /// Tokenizes the value of an app:dir= / app:path= rule token; other kinds pass through unchanged.
    /// </summary>
    public static string TokenizeRule(string token)
    {
        var eq = token.IndexOf('=');
        if (eq <= 0)
        {
            return token;
        }

        var kind = token[..eq];
        if (!kind.Equals("app:dir", StringComparison.OrdinalIgnoreCase)
            && !kind.Equals("app:path", StringComparison.OrdinalIgnoreCase))
        {
            return token;
        }

        return kind + "=" + Tokenize(token[(eq + 1)..]);
    }

    /// <summary>
    /// Normalizes an app:dir= / app:path= rule to a version-independent app:pkg= matcher for a Store/MSIX app,
    /// or to a portable %ENV% path otherwise. Other kinds pass through unchanged.
    /// </summary>
    public static string NormalizeAppRule(string token)
    {
        var eq = token.IndexOf('=');
        if (eq > 0)
        {
            var kind = token[..eq];
            if (kind.Equals("app:dir", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("app:path", StringComparison.OrdinalIgnoreCase))
            {
                var family = PackageFamilyFromPath(token[(eq + 1)..]);
                if (family is not null)
                {
                    return "app:pkg=" + family;
                }
            }
        }

        return TokenizeRule(token);
    }

    /// <summary>
    /// Returns the package family name (Name + PublisherId) for an image path under WindowsApps, dropping the
    /// version, architecture and resource id, so a rule keeps matching a Store/MSIX app across its auto-updates.
    /// Null when the path is not a packaged app.
    /// </summary>
    public static string? PackageFamilyFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        const string marker = "\\WindowsApps\\";
        var norm = path.Replace('/', '\\');
        var at = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var after = norm[(at + marker.Length)..];
        var slash = after.IndexOf('\\');
        var fullName = slash >= 0 ? after[..slash] : after;
        return PackageFamilyFromFullName(fullName);
    }

    // Package full name "Name_Version_Architecture_ResourceId_PublisherId" -> family "Name_PublisherId".
    private static string? PackageFamilyFromFullName(string fullName)
    {
        var parts = fullName.Split('_');
        if (parts.Length < 5)
        {
            return null;
        }

        var name = parts[0];
        var publisher = parts[^1];
        return name.Length > 0 && IsPublisherId(publisher) ? name + "_" + publisher : null;
    }

    // A package publisher id: 13 base32 characters.
    private static bool IsPublisherId(string segment)
    {
        if (segment.Length != 13)
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    private static string UsersRoot()
    {
        var pub = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(pub))
        {
            var root = System.IO.Path.GetDirectoryName(pub);
            if (!string.IsNullOrEmpty(root))
            {
                return root.TrimEnd('\\', '/');
            }
        }

        var drive = Environment.GetEnvironmentVariable("SystemDrive");
        return string.IsNullOrEmpty(drive) ? string.Empty : drive + "\\Users";
    }

    // (x86) first: its path has the plain Program Files path as a prefix.
    private static IEnumerable<(string Dir, string Token)> MachineFolders()
    {
        yield return (Env("ProgramFiles(x86)"), "%PROGRAMFILES(X86)%");
        yield return (Env("ProgramW6432"), "%PROGRAMFILES%");
        yield return (Env("ProgramFiles"), "%PROGRAMFILES%");
        yield return (Env("ProgramData"), "%PROGRAMDATA%");
        yield return (Env("SystemRoot"), "%SYSTEMROOT%");
    }

    private static string Env(string name)
    {
        return (Environment.GetEnvironmentVariable(name) ?? string.Empty).TrimEnd('\\', '/');
    }
}
