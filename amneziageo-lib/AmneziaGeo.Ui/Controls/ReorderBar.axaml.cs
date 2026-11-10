using Avalonia.Controls;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Кнопки перемещения карточки.
/// </summary>
internal sealed partial class ReorderBar : UserControl
{
    private int _columns = 1;

    /// <summary>
    /// ctor
    /// </summary>
    public ReorderBar()
    {
        InitializeComponent();
        LeftPart.Click += (_, _) => MoveRequested?.Invoke(-1);
        RightPart.Click += (_, _) => MoveRequested?.Invoke(1);
        UpPart.Click += (_, _) => MoveRequested?.Invoke(-_columns);
        DownPart.Click += (_, _) => MoveRequested?.Invoke(_columns);
        DonePart.Click += (_, _) => Completed?.Invoke();
        CancelPart.Click += (_, _) => Canceled?.Invoke();
        foreach (var button in new[] { LeftPart, UpPart, DownPart, RightPart, DonePart, CancelPart })
        {
            button.Focusable = !UiPlatform.IsTelevision;
        }
    }

    public event Action<int>? MoveRequested;

    public event Action? Completed;

    public event Action? Canceled;

    public void Update(bool active, int columns, Func<int, bool> canMove)
    {
        IsVisible = active;
        _columns = columns;
        LeftPart.IsVisible = columns > 1;
        RightPart.IsVisible = columns > 1;
        LeftPart.IsEnabled = canMove(-1);
        RightPart.IsEnabled = canMove(1);
        UpPart.IsEnabled = canMove(-columns);
        DownPart.IsEnabled = canMove(columns);
    }
}
