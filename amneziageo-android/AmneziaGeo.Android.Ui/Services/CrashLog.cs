using System;
using System.Threading.Tasks;

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
