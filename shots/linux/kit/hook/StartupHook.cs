using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

/// <summary>
/// Помощник набора кадров: по запросу в файле отдаёт дерево элементов окна и листает его.
/// </summary>
internal class StartupHook
{
    private static readonly List<Visual> Last = new List<Visual>();

    /// <summary>
    /// Ждёт запросы в папке AGSHOT_DIR.
    /// </summary>
    public static void Initialize()
    {
        var folder = Environment.GetEnvironmentVariable("AGSHOT_DIR");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var thread = new Thread(() => Serve(folder)) { IsBackground = true, Name = "agshot" };
        thread.Start();
    }

    // Отвечает на запросы из файла req ответом в файле res.
    private static void Serve(string folder)
    {
        var request = Path.Combine(folder, "req");
        var answer = Path.Combine(folder, "res");
        while (true)
        {
            Thread.Sleep(50);
            var command = Take(request);
            if (command == null)
            {
                continue;
            }

            var temp = answer + ".tmp";
            File.WriteAllText(temp, Run(command));
            File.Move(temp, answer, true);
        }
    }

    private static string Take(string request)
    {
        try
        {
            if (!File.Exists(request))
            {
                return null;
            }

            var text = File.ReadAllText(request).Trim();
            File.Delete(request);
            return text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Выполняет запрос в потоке окна после раскладки и отрисовки; до главного окна диспетчер не трогает.
    private static string Run(string command)
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.MainWindow == null)
        {
            return Reply("error", "not ready");
        }

        try
        {
            return Dispatcher.UIThread.InvokeAsync(() => Handle(command), DispatcherPriority.Background).GetTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return Reply("error", ex.ToString());
        }
    }

    private static string Handle(string command)
    {
        var parts = command.Split(' ', 3);
        switch (parts[0])
        {
            case "tree":
                return Tree();
            case "show":
                Pick(parts[1]).BringIntoView();
                return Reply("ok", "");
            case "offset":
                var viewer = (ScrollViewer)Pick(parts[1]);
                viewer.Offset = new Vector(viewer.Offset.X, double.Parse(parts[2], CultureInfo.InvariantCulture));
                return Reply("ok", "");
            case "focus":
                Pick(parts[1]).Focus();
                return Reply("ok", "");
            case "text":
                ((TextBox)Pick(parts[1])).Text = parts.Length > 2 ? parts[2] : "";
                return Reply("ok", "");
            default:
                return Reply("error", "unknown: " + command);
        }
    }

    private static Control Pick(string id)
    {
        return (Control)Last[int.Parse(id, CultureInfo.InvariantCulture)];
    }

    private static string Reply(string name, string text)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString(name, text);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Окна и их видимые элементы: тип, имя, классы, границы, видимая часть, текст, отметка.
    private static string Tree()
    {
        Last.Clear();
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var windows = lifetime?.Windows.Where(window => window.IsVisible).ToList() ?? new List<Window>();
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartArray("windows");
            foreach (var window in windows)
            {
                json.WriteStartObject();
                json.WriteString("title", window.Title);
                json.WriteNumber("x", window.Position.X);
                json.WriteNumber("y", window.Position.Y);
                json.WriteNumber("w", window.ClientSize.Width);
                json.WriteNumber("h", window.ClientSize.Height);
                json.WriteNumber("scale", window.RenderScaling);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("nodes");
            for (var index = 0; index < windows.Count; index++)
            {
                var window = windows[index];
                Walk(json, window, window, -1, index, new Rect(window.ClientSize));
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Walk(Utf8JsonWriter json, Visual visual, Window window, int parent, int win, Rect clip)
    {
        if (!visual.IsVisible)
        {
            return;
        }

        var transform = visual.TransformToVisual(window);
        if (transform == null)
        {
            return;
        }

        var box = new Rect(visual.Bounds.Size).TransformToAABB(transform.Value);
        var seen = box.Intersect(clip);
        var id = Last.Count;
        Last.Add(visual);
        json.WriteStartObject();
        json.WriteNumber("i", id);
        json.WriteNumber("p", parent);
        json.WriteNumber("win", win);
        json.WriteString("t", visual.GetType().Name);
        if (visual is StyledElement element)
        {
            if (!string.IsNullOrEmpty(element.Name))
            {
                json.WriteString("n", element.Name);
            }

            if (element.Classes.Count > 0)
            {
                json.WriteString("c", string.Join(" ", element.Classes));
            }
        }

        Box(json, "b", box);
        Box(json, "v", seen);
        Describe(json, visual);
        json.WriteEndObject();
        var inner = visual.ClipToBounds ? seen : clip;
        foreach (var child in visual.GetVisualChildren())
        {
            Walk(json, child, window, id, win, inner);
        }
    }

    private static void Describe(Utf8JsonWriter json, Visual visual)
    {
        if (visual is TextBlock block)
        {
            Text(json, "text", Content(block));
        }

        if (visual is TextBox box)
        {
            Text(json, "text", box.Text);
            Text(json, "hint", box.Watermark);
        }

        if (visual is ToggleButton toggle && toggle.IsChecked.HasValue)
        {
            json.WriteBoolean("chk", toggle.IsChecked.Value);
        }

        if (visual is ListBoxItem item)
        {
            json.WriteBoolean("sel", item.IsSelected);
        }

        if (visual is ScrollViewer viewer)
        {
            json.WriteNumber("oy", viewer.Offset.Y);
            json.WriteNumber("ey", viewer.Extent.Height);
            json.WriteNumber("vy", viewer.Viewport.Height);
        }

        if (visual is Control control)
        {
            Text(json, "tip", ToolTip.GetTip(control) as string);
            if (!control.IsEffectivelyEnabled)
            {
                json.WriteBoolean("off", true);
            }

            if (control.IsFocused)
            {
                json.WriteBoolean("focus", true);
            }
        }
    }

    private static string Content(TextBlock block)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            return block.Text;
        }

        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is Run run)
            {
                text.Append(run.Text);
            }
            else if (inline is LineBreak)
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private static void Text(Utf8JsonWriter json, string name, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            json.WriteString(name, text);
        }
    }

    private static void Box(Utf8JsonWriter json, string name, Rect rect)
    {
        json.WriteStartArray(name);
        json.WriteNumberValue(Math.Round(rect.X));
        json.WriteNumberValue(Math.Round(rect.Y));
        json.WriteNumberValue(Math.Round(rect.Right));
        json.WriteNumberValue(Math.Round(rect.Bottom));
        json.WriteEndArray();
    }
}
