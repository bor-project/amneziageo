using Terminal.Gui.App;

namespace AmneziaGeo.Linux.Cli.Tui;

/// <summary>
/// Full-screen console UI over the agent.
/// </summary>
internal static class TuiApp
{
    /// <summary>
    /// Runs the console UI until the user quits.
    /// </summary>
    public static Task<int> RunAsync(AgentClient agent)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Output.Error("the console UI needs a terminal; run it without redirecting input or output");
            return Task.FromResult(Exit.Usage);
        }

        Application.Init();
        try
        {
            using var shell = new Shell(agent);
            Application.Run(shell);
        }
        finally
        {
            Application.Shutdown();
        }

        return Task.FromResult(Exit.Ok);
    }
}
