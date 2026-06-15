namespace AmneziaGeo.Windows.Launcher;

/// <summary>
/// Effective launcher options: appsettings.json defaults overridden by command-line flags.
/// </summary>
internal sealed class LaunchOptions
{
    /// <summary>
    /// Whether to start the backend agent.
    /// </summary>
    public bool RunService { get; private set; }

    /// <summary>
    /// Whether to start the desktop UI.
    /// </summary>
    public bool RunUi { get; private set; }

    /// <summary>
    /// The balancer or single-config name the agent should drive.
    /// </summary>
    public string? Target { get; private set; }

    /// <summary>
    /// A wg-quick config to register (under its file name) before launching.
    /// </summary>
    public string? ConfigPath { get; private set; }

    /// <summary>
    /// Paths to default Amnezia configs (files or directories) to register on startup.
    /// </summary>
    public IReadOnlyList<string> ConfigPaths { get; private set; } = [];

    /// <summary>
    /// Resolves the effective options from configuration defaults and command-line overrides.
    /// </summary>
    public static LaunchOptions Resolve(LauncherOptions defaults, string[] args)
    {
        var options = new LaunchOptions
        {
            RunService = defaults.RunService,
            RunUi = defaults.RunUi,
            Target = string.IsNullOrWhiteSpace(defaults.Target) ? null : defaults.Target,
            ConfigPaths = defaults.ConfigPaths,
        };

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
            }
        }

        if (options.ConfigPath is not null && options.Target is null)
        {
            options.Target = Path.GetFileNameWithoutExtension(options.ConfigPath);
        }

        return options;
    }
}
