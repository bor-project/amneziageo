using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AmneziaGeo.Linux.Ui;

/// <summary>
/// Одно окно на пользователя: второй запуск просит открытое выйти вперёд и завершается. Владение держит
/// файл-замок, просьба едет по сокету именованного канала.
/// </summary>
internal static class SingleInstance
{
    private const string PipeName = "AmneziaGeo.Ui.Activate";

    // Сколько ждать ответа открытого окна, прежде чем считать замок брошенным.
    private const int HandoverMs = 700;

    // Держится всё время жизни процесса: закрытый поток снимает замок.
    private static FileStream? _lock;

    /// <summary>
    /// Занимает место единственного экземпляра; false, когда запуск отдан уже открытому окну.
    /// </summary>
    public static bool TryAcquire()
    {
        _lock = TryLock();
        if (_lock is not null)
        {
            Clear();
            return true;
        }

        // Замок занят, но никто не ответил - его держит зависший запуск: открываем своё окно.
        return !Wake();
    }

    /// <summary>
    /// Отвечает поздним запускам: выводит окно вперёд.
    /// </summary>
    public static void StartListening(Window window)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() => Raise(window)).GetTask().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Оборванная связь не должна снимать слушателя.
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        });
    }

    // Просит открытое окно выйти вперёд; false, когда никто не ответил.
    private static bool Wake()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(HandoverMs);
            client.WriteByte(1);
            client.Flush();
            return true;
        }
        catch (Exception)
        {
            // Открытого окна нет.
            return false;
        }
    }

    // Поднимает окно из свёрнутого состояния и отдаёт ему фокус.
    private static void Raise(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        // Просьбу фонового процесса менеджер окон отдаёт неохотно: короткий Topmost поднимает окно наверняка.
        window.Topmost = true;
        window.Activate();
        window.Topmost = false;
    }

    // Берёт файл-замок владения; null, когда его держит другой запуск.
    private static FileStream? TryLock()
    {
        try
        {
            if (Path.GetDirectoryName(LockPath) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Убирает сокет упавшего запуска: на занятом пути свой сервер не поднимется.
    private static void Clear()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Чужой файл на этом пути трогать нечем.
        }
        catch (UnauthorizedAccessException)
        {
            // Чужой файл на этом пути трогать нечем.
        }
    }

    private static string LockPath
    {
        get
        {
            var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = string.IsNullOrEmpty(data) ? Path.GetTempPath() : Path.Combine(data, "AmneziaGeo");
            return Path.Combine(root, "ui.lock");
        }
    }
}
