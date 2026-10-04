using System.Reflection;
using Avalonia.Threading;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Останавливает периодические таймеры, заведённые через DispatcherTimer.Run, пока окна нет на экране.
/// </summary>
internal static class HiddenTimers
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly List<DispatcherTimer> _paused = [];

    /// <summary>
    /// Останавливает работающие периодические таймеры; отвечает, сколько их остановлено.
    /// </summary>
    public static int Pause()
    {
        foreach (var timer in Running().Where(Periodic))
        {
            timer.Stop();
            _paused.Add(timer);
        }

        return _paused.Count;
    }

    /// <summary>
    /// Запускает остановленные таймеры снова; отвечает, сколько их запущено.
    /// </summary>
    public static int Resume()
    {
        var count = _paused.Count;
        foreach (var timer in _paused)
        {
            timer.Start();
        }

        _paused.Clear();
        return count;
    }

    // Работающие таймеры диспетчера окна.
    private static List<DispatcherTimer> Running()
    {
        var dispatcher = Dispatcher.UIThread;
        return [.. dispatcher.GetType().GetFields(Any)
            .Select(field => field.GetValue(dispatcher))
            .OfType<IEnumerable<DispatcherTimer>>()
            .SelectMany(timers => timers)
            .Where(timer => timer.IsEnabled)];
    }

    // Заведён ли таймер вызовом DispatcherTimer.Run.
    private static bool Periodic(DispatcherTimer timer)
    {
        return typeof(DispatcherTimer).GetField(nameof(DispatcherTimer.Tick), Any)?.GetValue(timer) is Delegate tick
            && tick.GetInvocationList().All(handler =>
                handler.Method.DeclaringType?.DeclaringType == typeof(DispatcherTimer)
                && handler.Method.Name.Contains("<Run>", StringComparison.Ordinal));
    }
}
