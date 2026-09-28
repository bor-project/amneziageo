using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Runtime;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Records the errors that otherwise take the process down without a trace: the java side, the app domain and
/// tasks nobody awaited. The row lands in the app log, so the diagnostics archive carries it.
/// </summary>
internal static class CrashLog
{
    private const string Source = "ui";
    private const int FlushMs = 2000;
    private const string ExitPrefs = "crashlog";
    private const string ExitSeenKey = "exit-seen";
    private const int ExitsRead = 16;
    private const int TraceLines = 80;

    private static bool _installed;

    /// <summary>
    /// Subscribes the handlers once.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Write("the ui thread raised an error it did not handle", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("the app raised an error it did not handle", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("a background task failed and nobody awaited it", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Пишет в журнал, как процессы приложения завершались с прошлого запуска: зависание со стеком главного потока,
    /// падение, выгрузка по памяти.
    /// </summary>
    public static void ReportPastExits(AndroidAgentLog log)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return;
        }

        try
        {
            var context = global::Android.App.Application.Context;
            var manager = context.GetSystemService(Context.ActivityService) as ActivityManager;
            var exits = manager?.GetHistoricalProcessExitReasons(context.PackageName, 0, ExitsRead);
            if (exits is null || exits.Count == 0)
            {
                return;
            }

            var prefs = context.GetSharedPreferences(ExitPrefs, FileCreationMode.Private);
            var seen = prefs?.GetLong(ExitSeenKey, 0) ?? 0;
            var newest = seen;
            foreach (var exit in exits)
            {
                if (exit.Timestamp <= seen)
                {
                    continue;
                }

                newest = Math.Max(newest, exit.Timestamp);
                if (IsFailure((ApplicationExitInfoReason)exit.Reason))
                {
                    log.Error(Source, Describe(exit));
                }
            }

            prefs?.Edit()?.PutLong(ExitSeenKey, newest)?.Commit();
        }
        catch (Exception ex)
        {
            log.Error(Source, "the past exits of the app could not be read", ex);
        }
    }

    // Причины, по которым процесс ушёл не сам и не по воле пользователя.
    [SupportedOSPlatform("android30.0")]
    private static bool IsFailure(ApplicationExitInfoReason reason) => reason
        is ApplicationExitInfoReason.Anr
        or ApplicationExitInfoReason.Crash
        or ApplicationExitInfoReason.CrashNative
        or ApplicationExitInfoReason.LowMemory
        or ApplicationExitInfoReason.ExcessiveResourceUsage
        or ApplicationExitInfoReason.InitializationFailure;

    // Строка о завершении процесса: когда, почему, сколько памяти он держал; у зависания и стек главного потока.
    [SupportedOSPlatform("android30.0")]
    private static string Describe(ApplicationExitInfo exit)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(exit.Timestamp).ToLocalTime();
        var reason = (ApplicationExitInfoReason)exit.Reason;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"process {exit.ProcessName} ended at {at:yyyy-MM-dd HH:mm:ss} ({reason}): {exit.Description}; pss {exit.Pss / 1024} MB, rss {exit.Rss / 1024} MB");
        if (reason == ApplicationExitInfoReason.Anr && MainThread(exit) is { Length: > 0 } stack)
        {
            text.AppendLine().Append(stack);
        }

        return text.ToString();
    }

    // Блок главного потока из трассы, которую система сняла с зависшего процесса.
    [SupportedOSPlatform("android30.0")]
    private static string MainThread(ApplicationExitInfo exit)
    {
        using var stream = exit.TraceInputStream;
        if (stream is null)
        {
            return string.Empty;
        }

        // Нативный дамп подписывает главный поток именем процесса и номером, равным pid.
        var nativeMain = $"sysTid={exit.Pid}";
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line && lines.Count < TraceLines)
        {
            if (lines.Count == 0
                && !(line.StartsWith('"') && (line.StartsWith("\"main\"", StringComparison.Ordinal) || line.EndsWith(nativeMain, StringComparison.Ordinal))))
            {
                continue;
            }

            if (line.Length == 0)
            {
                break;
            }

            var build = line.IndexOf(" (BuildId: ", StringComparison.Ordinal);
            lines.Add(build > 0 ? line[..build] : line);
        }

        return string.Join(Environment.NewLine, lines);
    }

    // Строка уходит в журнал приложения и ждёт записи: следом процесс обычно умирает.
    private static void Write(string message, Exception? error)
    {
        var log = AndroidAgentLog.Current;
        if (log is null)
        {
            global::Android.Util.Log.Error("AmneziaGeo", error is null ? message : $"{message}{Environment.NewLine}{error}");
            return;
        }

        log.Error(Source, message, error);
        try
        {
            log.Store.FlushAsync().Wait(FlushMs);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("AmneziaGeo", $"the crash row was not written{Environment.NewLine}{ex}");
        }
    }
}
