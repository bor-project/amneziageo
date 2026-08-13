namespace AmneziaGeo.Decl;

/// <summary>
/// One account the local proxy admits clients under.
/// </summary>
/// <param name="User">Name the client sends.</param>
/// <param name="Password">Password that goes with the name.</param>
public sealed record ProxyAccount(string User, string Password);

/// <summary>
/// The stored form of the proxy accounts: one "user:password" per line. The first colon of a line ends the name,
/// so a password may carry colons of its own.
/// </summary>
public static class ProxyCredentials
{
    /// <summary>
    /// Reads the accounts off the stored text.
    /// </summary>
    public static IReadOnlyList<ProxyAccount> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var accounts = new List<ProxyAccount>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            accounts.Add(new ProxyAccount(line[..colon], line[(colon + 1)..]));
        }

        return accounts;
    }

    /// <summary>
    /// Writes the accounts as the stored text, leaving out the nameless ones.
    /// </summary>
    public static string Compose(IEnumerable<ProxyAccount> accounts)
    {
        var lines = new List<string>();
        foreach (var account in accounts)
        {
            var user = Clean(account.User, ':');
            if (user.Length == 0)
            {
                continue;
            }

            lines.Add($"{user}:{Clean(account.Password)}");
        }

        return string.Join('\n', lines);
    }

    private static string Clean(string value, char extra = '\0')
    {
        return new string([.. value.Where(c => c != '\n' && c != '\r' && c != extra)]).Trim();
    }
}
