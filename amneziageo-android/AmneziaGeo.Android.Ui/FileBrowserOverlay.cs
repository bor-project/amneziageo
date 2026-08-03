using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AmneziaGeo.Localization;
using Button = Avalonia.Controls.Button;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Built-in file browser for devices without a document picker (Android TV). Lists only what this app may
/// actually read: its own folders on every storage volume, plus any shared folder still open to it.
/// </summary>
internal sealed class FileBrowserOverlay
{
    private readonly Panel _host;
    private readonly string[] _extensions;
    private readonly TaskCompletionSource<string?> _result = new();
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _location;
    private readonly Border _overlay;
    private readonly List<Control> _suspended = [];
    private string? _folder;

    private FileBrowserOverlay(Panel host, string title, IReadOnlyList<string> extensions)
    {
        _host = host;
        _extensions = extensions.Select(ext => "." + ext.TrimStart('.')).ToArray();

        var caption = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16 };
        _location = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
        _location.Classes.Add("muted");

        var cancel = new Button
        {
            Content = Loc.Instance.Get("Main_CancelButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 0),
        };
        cancel.Classes.Add("softbtn");
        cancel.Click += (_, _) => Finish(null);

        var header = new StackPanel { Children = { caption, _location } };
        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(cancel, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(cancel);
        panel.Children.Add(new ScrollViewer { Content = _list });

        _overlay = new Border { Child = panel };
        _overlay.Background = _overlay.TryFindResource("AgPanelBrush", out var brush) && brush is IBrush found
            ? found
            : new SolidColorBrush(Color.FromRgb(0x1a, 0x1c, 0x20));
        // Disables what the overlay covers: directional focus otherwise walks into the screen behind it.
        foreach (var child in host.Children)
        {
            if (child.IsEnabled)
            {
                child.IsEnabled = false;
                _suspended.Add(child);
            }
        }

        host.Children.Add(_overlay);
        MainActivity.Resumed += OnActivityResumed;
        Populate();
    }

    /// <summary>
    /// The browser currently on screen, if any.
    /// </summary>
    public static FileBrowserOverlay? Current { get; private set; }

    /// <summary>
    /// Shows the browser over the host panel and completes with the chosen path or null.
    /// </summary>
    public static Task<string?> ShowAsync(Panel host, string title, IReadOnlyList<string> extensions)
    {
        Current?.Finish(null);
        var browser = new FileBrowserOverlay(host, title, extensions);
        Current = browser;
        return browser._result.Task;
    }

    /// <summary>
    /// Steps out of the current folder, or leaves the browser when already at the folder list.
    /// </summary>
    public void Back()
    {
        if (_folder is null)
        {
            Finish(null);
            return;
        }

        _folder = Roots().Any(root => root.Path == _folder) ? null : Path.GetDirectoryName(_folder);
        Populate();
    }

    private void Populate()
    {
        _list.Children.Clear();

        if (_folder is null)
        {
            _location.Text = Loc.Instance.Get("FileBrowser_ScopedHint");
            _location.IsVisible = !HasAllFiles();
            if (!HasAllFiles() && AllFilesIntent() is { } grant)
            {
                _list.Children.Add(Row(Loc.Instance.Get("FileBrowser_AllFilesAccess"), null, () => Launch(grant)));
            }

            foreach (var (label, path) in Roots())
            {
                _list.Children.Add(Row(label, path, () => Enter(path)));
            }
        }
        else
        {
            _location.Text = _folder;
            _location.IsVisible = true;
            _list.Children.Add(Row("..", null, Back));

            var (folders, files) = Read(_folder, _extensions);
            foreach (var folder in folders)
            {
                _list.Children.Add(Row(Path.GetFileName(folder) + "/", null, () => Enter(folder)));
            }

            foreach (var file in files)
            {
                _list.Children.Add(Row(Path.GetFileName(file), null, () => Finish(file)));
            }

            if (folders.Length == 0 && files.Length == 0)
            {
                var empty = new TextBlock { Text = Loc.Instance.Get("FileBrowser_Empty"), Margin = new Thickness(4, 10, 0, 0) };
                empty.Classes.Add("muted");
                _list.Children.Add(empty);
            }
        }

        FocusFirstRow();
    }

    // Seats D-pad focus on the first row, otherwise a remote has nothing to move from. A freshly built row
    // only takes focus once it is loaded.
    private void FocusFirstRow()
    {
        if (_list.Children.OfType<Button>().FirstOrDefault() is not { } first)
        {
            return;
        }

        if (first.IsLoaded)
        {
            first.Focus(NavigationMethod.Directional);
            return;
        }

        first.Loaded += OnRowLoaded;
    }

    private static void OnRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control row)
        {
            row.Loaded -= OnRowLoaded;
            row.Focus(NavigationMethod.Directional);
        }
    }

    private void Enter(string folder)
    {
        _folder = folder;
        Populate();
    }

    private static Button Row(string label, string? detail, Action activate)
    {
        var caption = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new StackPanel { Children = { caption } };
        if (detail is not null)
        {
            var path = new TextBlock { Text = detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.7 };
            content.Children.Add(path);
        }

        var row = new Button { Content = content };
        row.Classes.Add("mobile-select-option");
        row.Click += (_, _) => activate();
        return row;
    }

    // Directories this app may enumerate: its own folders on each volume first, then anything shared that
    // the platform has not closed off.
    private static IReadOnlyList<(string Label, string Path)> Roots()
    {
        var context = global::Android.App.Application.Context;
        var roots = new List<(string Label, string Path)>();

        foreach (var dir in context.GetExternalFilesDirs(null) ?? [])
        {
            Add(roots, dir?.AbsolutePath, Loc.Instance.Get("FileBrowser_AppFolder"));
        }

        Add(roots, context.FilesDir?.AbsolutePath, Loc.Instance.Get("FileBrowser_AppInternal"));

        // Shared folders list nothing without all-files access: scoped storage hides every file the app does not own.
        if (HasAllFiles())
        {
            Add(roots, global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath, Loc.Instance.Get("FileBrowser_SharedStorage"));
            Add(roots, global::Android.OS.Environment
                .GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath, "Download");
        }

        return roots;
    }

    private static bool HasAllFiles()
        => !OperatingSystem.IsAndroidVersionAtLeast(30) || global::Android.OS.Environment.IsExternalStorageManager;

    // The system screen that grants all-files access, when this device has one.
    private static global::Android.Content.Intent? AllFilesIntent()
    {
        var context = global::Android.App.Application.Context;
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || context.PackageManager is not { } manager)
        {
            return null;
        }

        var intent = new global::Android.Content.Intent(
            global::Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
            global::Android.Net.Uri.Parse("package:" + context.PackageName));
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        return intent.ResolveActivity(manager) is null ? null : intent;
    }

    private static void Launch(global::Android.Content.Intent intent)
    {
        try
        {
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (global::Android.Content.ActivityNotFoundException)
        {
        }
    }

    // Re-reads the roots after the user comes back from the all-files-access screen.
    private void OnActivityResumed()
    {
        if (_folder is null)
        {
            Dispatcher.UIThread.Post(Populate);
        }
    }

    // Keeps a root only when it exists and this app really can list it.
    private static void Add(List<(string Label, string Path)> roots, string? path, string label)
    {
        if (path is null || roots.Any(root => root.Path == path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        roots.Add((label, path));
    }

    private static (string[] Folders, string[] Files) Read(string folder, string[] extensions)
    {
        try
        {
            var folders = Directory.GetDirectories(folder)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var files = Directory.GetFiles(folder)
                .Where(file => extensions.Length == 0
                    || extensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return (folders, files);
        }
        catch (UnauthorizedAccessException)
        {
            return ([], []);
        }
        catch (IOException)
        {
            return ([], []);
        }
    }

    private void Finish(string? path)
    {
        MainActivity.Resumed -= OnActivityResumed;
        _host.Children.Remove(_overlay);
        foreach (var control in _suspended)
        {
            control.IsEnabled = true;
        }

        _suspended.Clear();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        _result.TrySetResult(path);
    }
}
