using System.ComponentModel;
using System.Diagnostics;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Runs helper binaries and returns their exit code with the merged output.
/// </summary>
internal static class Shell
{
    /// <summary>
    /// Runs a command and returns its exit code with the merged output.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string file, CancellationToken ct, params string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Start(info);
        if (process is null)
        {
            return (-1, $"could not start {file}");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, (stdout + stderr).Trim());
    }

    // A helper that is not installed fails the one call that needed it, the way a helper that refused would,
    // instead of throwing through whatever loop asked.
    private static Process? Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the token following a keyword in iproute2 output.
    /// </summary>
    public static string? Token(string output, string keyword)
    {
        var tokens = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = Array.IndexOf(tokens, keyword);
        return index >= 0 && index + 1 < tokens.Length ? tokens[index + 1] : null;
    }
}
