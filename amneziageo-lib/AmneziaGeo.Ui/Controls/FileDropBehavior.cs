using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Приём файлов драгом: включает drop на контроле и вызывает команду со списком путей.
/// </summary>
internal static class FileDropBehavior
{
    /// <summary>
    /// Команда, получающая список путей брошенных файлов.
    /// </summary>
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(FileDropBehavior));

    static FileDropBehavior()
    {
        CommandProperty.Changed.AddClassHandler<Control>(OnCommandChanged);
    }

    public static void SetCommand(Control target, ICommand? value) => target.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(Control target) => target.GetValue(CommandProperty);

    private static void OnCommandChanged(Control target, AvaloniaPropertyChangedEventArgs e)
    {
        DragDrop.SetAllowDrop(target, e.NewValue is not null);
        target.RemoveHandler(DragDrop.DragEnterEvent, OnDragOver);
        target.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        target.RemoveHandler(DragDrop.DropEvent, OnDrop);
        if (e.NewValue is not null)
        {
            target.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
            target.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            target.AddHandler(DragDrop.DropEvent, OnDrop);
        }
    }

    // Only file drags are claimed; other payloads pass through so text drops still reach inner textboxes.
    // The entering drag is answered too: an unanswered enter leaves the cursor refusing the drop.
    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!HasFiles(e))
        {
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control target || !HasFiles(e))
        {
            return;
        }

        var command = GetCommand(target);
        var paths = ExtractPaths(e);
        if (command is not null && paths.Count > 0 && command.CanExecute(paths))
        {
            command.Execute(paths);
        }

        e.Handled = true;
    }

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private static IReadOnlyList<string> ExtractPaths(DragEventArgs e)
    {
        var paths = new List<string>();
        Collect(e.DataTransfer.TryGetFiles(), paths);
        return paths;
    }

    private static void Collect(IEnumerable<IStorageItem>? files, List<string> paths)
    {
        if (files is null)
        {
            return;
        }

        foreach (var item in files)
        {
            if (item is IStorageFile file && file.TryGetLocalPath() is { } path)
            {
                paths.Add(path);
            }
        }
    }
}