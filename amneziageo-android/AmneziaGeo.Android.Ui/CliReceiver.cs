using System.Globalization;
using System.Text;
using Android.App;
using Android.Content;
using AmneziaGeo.Android.Ui.Services;
using AmneziaGeo.Cli;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Runs a console command that arrived as a broadcast and answers in the reply data.
/// </summary>
[BroadcastReceiver(Name = "org.amneziageo.android.CliReceiver", Exported = true, Enabled = true, Permission = ShellPermission)]
[IntentFilter([CliReceiver.Action])]
public sealed class CliReceiver : BroadcastReceiver
{
    /// <summary>
    /// Action the broadcast carries.
    /// </summary>
    public const string Action = "org.amneziageo.android.CLI";

    /// <summary>
    /// Held by the adb shell and by no ordinary application.
    /// </summary>
    private const string ShellPermission = "android.permission.DUMP";

    private const string Tag = "AmneziaGeoCli";

    // The reply travels through a Binder transaction; the whole answer goes to logcat and to --es out.
    private const int ReplyLimit = 128 * 1024;

    // A background broadcast may take about a minute before the system calls it dead.
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(50);

    // Output is process-global state, so one command runs at a time.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc/>
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (GoAsync() is not { } pending)
        {
            global::Android.Util.Log.Error(Tag, "the broadcast could not be held open");
            return;
        }

        var args = Arguments(intent);
        var outPath = intent?.GetStringExtra("out");
        _ = Task.Run(async () =>
        {
            var code = Exit.Failed;
            var text = string.Empty;
            try
            {
                (code, text) = await RunAsync(args).ConfigureAwait(false);
                Write(outPath, text);
            }
            catch (Exception ex)
            {
                text = $"{ex.GetType().Name}: {ex.Message}";
                global::Android.Util.Log.Error(Tag, ex.ToString());
            }
            finally
            {
                Mirror(text);
                pending.ResultCode = (Result)code;
                pending.ResultData = text.Length > ReplyLimit
                    ? text[..ReplyLimit] + $"\n[cut at {ReplyLimit.ToString(CultureInfo.InvariantCulture)} bytes: pass --es out <path> for the whole answer]"
                    : text;
                pending.Finish();
            }
        });
    }

    private static async Task<(int Code, string Text)> RunAsync(string[] args)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        var previous = Output.Sink;
        var buffer = new BufferConsoleSink();
        try
        {
            Output.Sink = buffer;
            using var deadline = new CancellationTokenSource(_budget);
            var code = await CliRunner.RunAsync(args, new AndroidCliHost(), deadline.Token).ConfigureAwait(false);
            return (code, buffer.ToString());
        }
        finally
        {
            Output.Sink = previous;
            Output.Json = false;
            Output.Quiet = false;
            _gate.Release();
        }
    }

    // Takes an exact argument vector when given one, else splits a single quoted line.
    private static string[] Arguments(Intent? intent)
    {
        if (intent?.GetStringArrayExtra("args") is { Length: > 0 } vector)
        {
            return [.. vector.Where(argument => argument is not null).Select(argument => argument!)];
        }

        return Split(intent?.GetStringExtra("cmd") ?? "help");
    }

    private static string[] Split(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var character in line)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }

    private static void Write(string? path, string text)
    {
        if (path is not { Length: > 0 })
        {
            return;
        }

        try
        {
            File.WriteAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            global::Android.Util.Log.Warn(Tag, $"could not write {path}: {ex.Message}");
        }
    }

    // logcat drops anything past a few kilobytes in one entry.
    private static void Mirror(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            for (var start = 0; start < Math.Max(line.Length, 1); start += 2000)
            {
                global::Android.Util.Log.Info(Tag, line[start..Math.Min(line.Length, start + 2000)]);
            }
        }
    }
}
