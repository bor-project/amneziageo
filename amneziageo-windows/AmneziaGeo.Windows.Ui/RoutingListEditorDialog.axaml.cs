using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Windows.Ui.ViewModels;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Dialog for creating or editing a shared routing list.
/// </summary>
public sealed partial class RoutingListEditorDialog : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public RoutingListEditorDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is RoutingListEditorViewModel vm)
        {
            await vm.LoadAsync();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutingListEditorViewModel vm)
        {
            Close(false);
            return;
        }

        var ok = await vm.SaveAsync();
        if (ok)
        {
            Close(true);
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutingListEditorViewModel vm)
        {
            return;
        }

        var confirm = new Window
        {
            Title = "Удалить список?",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var stack = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        stack.Children.Add(new TextBlock
        {
            Text = "Список будет удалён. Профили, ссылающиеся на него, потеряют привязку.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        var cancel = new Button { Content = "Отмена" };
        var ok = new Button { Content = "Удалить" };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);
        confirm.Content = stack;
        var confirmed = false;
        cancel.Click += (_, _) => confirm.Close();
        ok.Click += (_, _) => { confirmed = true; confirm.Close(); };
        await confirm.ShowDialog(this);
        if (!confirmed)
        {
            return;
        }

        var done = await vm.DeleteAsync();
        if (done)
        {
            Close(true);
        }
    }
}
