namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Options that apply to every command, wherever they appear on the line.
/// </summary>
internal sealed class GlobalOptions
{
    private static readonly string[] _switches = ["--json", "--quiet", "--help", "-h", "--version"];
    private static readonly string[] _valued = ["--lang", "--timeout"];

    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<string> _rest = [];

    private GlobalOptions()
    {
    }

    /// <summary>
    /// The command and its own arguments, in order.
    /// </summary>
    public IReadOnlyList<string> Rest => _rest;

    /// <summary>
    /// Parse error, or null.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Lifts the global options out of the command line, leaving everything else untouched.
    /// </summary>
    public static GlobalOptions Split(string[] args)
    {
        var options = new GlobalOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            var separator = argument.IndexOf('=');
            var name = separator > 0 ? argument[..separator] : argument;
            var inline = separator > 0 ? argument[(separator + 1)..] : null;

            if (_switches.Contains(name))
            {
                options._flags.Add(name.TrimStart('-'));
                continue;
            }

            if (!_valued.Contains(name))
            {
                options._rest.Add(argument);
                continue;
            }

            if (inline is { Length: > 0 })
            {
                options._values[name.TrimStart('-')] = inline;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                options.Error = $"{name} needs a value";
                break;
            }

            options._values[name.TrimStart('-')] = args[++i];
        }

        return options;
    }

    /// <summary>
    /// Whether a global switch was given.
    /// </summary>
    public bool Has(string name) => _flags.Contains(name);

    /// <summary>
    /// Value of a global option, or null.
    /// </summary>
    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;
}
