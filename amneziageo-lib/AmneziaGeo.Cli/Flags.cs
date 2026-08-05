namespace AmneziaGeo.Cli;

/// <summary>
/// Named options and leftover positional arguments of one leaf command.
/// </summary>
public sealed class Flags
{
    private readonly Dictionary<string, List<string>> _named = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    private Flags()
    {
    }

    /// <summary>
    /// Positional arguments in order.
    /// </summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>
    /// Parse error, or null when the arguments are well-formed.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Splits arguments into named options and positionals; names listed in switches take no value.
    /// </summary>
    public static Flags Parse(IReadOnlyList<string> args, params string[] switches)
    {
        var flags = new Flags();
        var toggles = new HashSet<string>(switches, StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                flags._positional.Add(argument);
                continue;
            }

            var separator = argument.IndexOf('=');
            var name = separator > 0 ? argument[2..separator] : argument[2..];
            var inline = separator > 0 ? argument[(separator + 1)..] : null;

            if (toggles.Contains(name))
            {
                flags.Add(name, inline ?? "on");
                continue;
            }

            if (inline is not null)
            {
                flags.Add(name, inline);
                continue;
            }

            if (i + 1 >= args.Count)
            {
                flags.Error ??= $"--{name} needs a value";
                return flags;
            }

            flags.Add(name, args[++i]);
        }

        return flags;
    }

    /// <summary>
    /// Whether a switch was given.
    /// </summary>
    public bool Has(string name) => _named.ContainsKey(name);

    /// <summary>
    /// Value of a named option, or null.
    /// </summary>
    public string? Value(string name) => _named.TryGetValue(name, out var values) ? values[^1] : null;

    /// <summary>
    /// Every value given for a repeatable option.
    /// </summary>
    public IReadOnlyList<string> Values(string name) => _named.TryGetValue(name, out var values) ? values : [];

    /// <summary>
    /// Rejects options outside the allowed set.
    /// </summary>
    public bool Allowed(params string[] names)
    {
        var known = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var name in _named.Keys)
        {
            if (!known.Contains(name))
            {
                Error ??= $"unknown option --{name}";
                return false;
            }
        }

        return Error is null;
    }

    private void Add(string name, string value)
    {
        if (!_named.TryGetValue(name, out var values))
        {
            values = [];
            _named[name] = values;
        }

        values.Add(value);
    }
}
