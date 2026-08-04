using AmneziaGeo.Localization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AmneziaGeo.Linux.Cli.Tui;

/// <summary>
/// Modal dialogs of the console UI.
/// </summary>
internal static class Prompt
{
    /// <summary>
    /// Asks for one line of text.
    /// </summary>
    public static string? Line(string title, string label, string initial = "")
    {
        var dialog = new Dialog { Title = title, Width = Dim.Percent(70), Height = 9 };
        var caption = new Label { Text = label, X = 1, Y = 1 };
        var field = new TextField { Text = initial, X = 1, Y = 3, Width = Dim.Fill(2) };
        var answer = default(string);

        var ok = new Button { Text = Loc.Instance.Get("Tui_Ok"), IsDefault = true, X = 1, Y = 5 };
        ok.Accepting += (_, _) =>
        {
            answer = field.Text;
            Application.RequestStop();
        };

        var cancel = new Button { Text = Loc.Instance.Get("Main_CancelButton"), X = 16, Y = 5 };
        cancel.Accepting += (_, _) => Application.RequestStop();

        dialog.Add(caption, field, ok, cancel);
        field.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
        return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
    }

    /// <summary>
    /// Asks for a block of text.
    /// </summary>
    public static string? Block(string title, string label, string initial = "")
    {
        var dialog = new Dialog { Title = title, Width = Dim.Percent(85), Height = Dim.Percent(80) };
        var caption = new Label { Text = label, X = 1, Y = 0 };
        var editor = new TextView { Text = initial, X = 1, Y = 2, Width = Dim.Fill(2), Height = Dim.Fill(3) };
        var answer = default(string);

        var ok = new Button { Text = Loc.Instance.Get("Tui_Ok"), X = 1, Y = Pos.AnchorEnd(1) };
        ok.Accepting += (_, _) =>
        {
            answer = editor.Text;
            Application.RequestStop();
        };

        var cancel = new Button { Text = Loc.Instance.Get("Main_CancelButton"), X = 16, Y = Pos.AnchorEnd(1) };
        cancel.Accepting += (_, _) => Application.RequestStop();

        dialog.Add(caption, editor, ok, cancel);
        editor.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
        return string.IsNullOrWhiteSpace(answer) ? null : answer;
    }

    /// <summary>
    /// Shows read-only text.
    /// </summary>
    public static void View(string title, string text)
    {
        var dialog = new Dialog { Title = title, Width = Dim.Percent(85), Height = Dim.Percent(80) };
        var viewer = new TextView { Text = text, ReadOnly = true, X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2) };
        var close = new Button { Text = Loc.Instance.Get("Main_CloseTooltip"), IsDefault = true, X = 1, Y = Pos.AnchorEnd(1) };
        close.Accepting += (_, _) => Application.RequestStop();

        dialog.Add(viewer, close);
        close.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
    }

    /// <summary>
    /// Asks a yes/no question.
    /// </summary>
    public static bool Confirm(string title, string message) =>
        MessageBox.Query(Application.Instance, title, message, Loc.Instance.Get("Main_ConfirmDeleteButton"), Loc.Instance.Get("Main_CancelButton")) == 0;

    /// <summary>
    /// Shows a failure.
    /// </summary>
    public static void Error(string message) =>
        MessageBox.ErrorQuery(Application.Instance, Loc.Instance.Get("Tui_Title"), message, Loc.Instance.Get("Tui_Ok"));

    /// <summary>
    /// Shows a result.
    /// </summary>
    public static void Info(string message) =>
        MessageBox.Query(Application.Instance, Loc.Instance.Get("Tui_Title"), message, Loc.Instance.Get("Tui_Ok"));

    /// <summary>
    /// Lets the user pick one of the given labels.
    /// </summary>
    public static int? Pick(string title, IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
        {
            return null;
        }

        var dialog = new Dialog { Title = title, Width = Dim.Percent(60), Height = Dim.Percent(60) };
        var list = new ListView { X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(2) };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>([.. labels]));
        var chosen = default(int?);

        var ok = new Button { Text = Loc.Instance.Get("Tui_Ok"), IsDefault = true, X = 1, Y = Pos.AnchorEnd(1) };
        ok.Accepting += (_, _) =>
        {
            chosen = list.SelectedItem;
            Application.RequestStop();
        };

        var cancel = new Button { Text = Loc.Instance.Get("Main_CancelButton"), X = 16, Y = Pos.AnchorEnd(1) };
        cancel.Accepting += (_, _) => Application.RequestStop();

        dialog.Add(list, ok, cancel);
        list.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
        return chosen is { } index && index >= 0 && index < labels.Count ? index : null;
    }
}
