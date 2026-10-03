using System.ComponentModel;
using System.Diagnostics;
using Terminal.Gui.App;

namespace AmneziaGeo.Linux.Cli.Tui;

/// <summary>
/// The desktop clipboard through the tool the session has: wl-clipboard under Wayland, xclip or xsel under X.
/// </summary>
internal sealed class SystemClipboard : ClipboardBase
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(3);

    private readonly string _reader;
    private readonly string[] _read;
    private readonly string _writer;
    private readonly string[] _write;

    private SystemClipboard(string reader, string[] read, string writer, string[] write)
    {
        _reader = reader;
        _read = read;
        _writer = writer;
        _write = write;
    }

    /// <inheritdoc/>
    public override bool IsSupported => true;

    /// <summary>
    /// The clipboard of this session, or null when it has no display or none of the tools.
    /// </summary>
    public static SystemClipboard? Find()
    {
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            && Which("wl-paste") is { } paste && Which("wl-copy") is { } copy)
        {
            return new SystemClipboard(paste, ["--no-newline"], copy, []);
        }

        if (Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 })
        {
            return null;
        }

        if (Which("xclip") is { } xclip)
        {
            return new SystemClipboard(xclip, ["-selection", "clipboard", "-o"], xclip, ["-selection", "clipboard", "-i"]);
        }

        return Which("xsel") is { } xsel
            ? new SystemClipboard(xsel, ["--clipboard", "--output"], xsel, ["--clipboard", "--input"])
            : null;
    }

    /// <inheritdoc/>
    protected override string GetClipboardDataImpl()
    {
        using var process = Start(_reader, _read, input: false);
        var text = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(_wait) || !text.Wait(_wait))
        {
            process.Kill();
            throw new InvalidOperationException($"{_reader} did not answer");
        }

        // An empty clipboard is an error to wl-paste and xclip alike, not a failure worth reporting.
        return process.ExitCode == 0 ? text.Result : string.Empty;
    }

    /// <inheritdoc/>
    protected override void SetClipboardDataImpl(string? text)
    {
        // wl-copy and xclip leave a child behind to serve the selection: it keeps the pipes open, so only the
        // tool itself is waited for.
        using var process = Start(_writer, _write, input: true);
        process.StandardInput.Write(text ?? string.Empty);
        process.StandardInput.Close();
        if (!process.WaitForExit(_wait))
        {
            throw new InvalidOperationException($"{_writer} did not finish");
        }
    }

    private static Process Start(string file, string[] args, bool input)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = input,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            return Process.Start(info) ?? throw new InvalidOperationException($"could not start {file}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"could not start {file}: {ex.Message}", ex);
        }
    }

    private static string? Which(string binary)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, binary);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
