namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Parsed launcher command-line options.
/// </summary>
internal sealed class LaunchOptions
{
    /// <summary>
    /// Whether to start the backend (agent) process.
    /// </summary>
    public bool RunService { get; private set; } = true;

    /// <summary>
    /// Whether to start the desktop UI process.
    /// </summary>
    public bool RunUi { get; private set; } = true;

    /// <summary>
    /// The balancer or single-config name the agent should drive.
    /// </summary>
    public string? Target { get; private set; }

    /// <summary>
    /// A wg-quick config to register (under its file name) before launching.
    /// </summary>
    public string? ConfigPath { get; private set; }

    /// <summary>
    /// Explicit path to AmneziaGeo.Windows.App.exe, overriding auto-location.
    /// </summary>
    public string? AppPath { get; private set; }

    /// <summary>
    /// Parses the launcher arguments.
    /// </summary>
    public static LaunchOptions Parse(string[] args)
    {
        var options = new LaunchOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--service-only" or "--service":
                    options.RunService = true;
                    options.RunUi = false;
                    break;
                case "--ui-only" or "--ui":
                    options.RunUi = true;
                    options.RunService = false;
                    break;
                case "--target" when i + 1 < args.Length:
                    options.Target = args[++i];
                    break;
                case "--config" when i + 1 < args.Length:
                    options.ConfigPath = args[++i];
                    break;
                case "--app" when i + 1 < args.Length:
                    options.AppPath = args[++i];
                    break;
            }
        }

        if (options.ConfigPath is not null && options.Target is null)
        {
            options.Target = Path.GetFileNameWithoutExtension(options.ConfigPath);
        }

        return options;
    }
}
