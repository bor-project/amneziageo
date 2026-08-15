using System.Threading.Tasks;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Правка одного значения диалогом поверх экрана. Живёт в оболочке.
/// </summary>
internal sealed partial class ValueEditorViewModel : ViewModelBase
{
    private TaskCompletionSource<string?>? _pending;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _watermark = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _multiline;

    [ObservableProperty]
    private bool _mono;

    /// <summary>
    /// Открывает диалог и отдаёт набранное значение либо null при отказе.
    /// </summary>
    public Task<string?> EditAsync(ValueEdit request)
    {
        // Новый запрос закрывает прежний: строка под ним уже не ждёт ответа.
        Complete(null);
        Title = request.Title ?? string.Empty;
        Description = request.Description ?? string.Empty;
        Watermark = request.Watermark ?? string.Empty;
        Text = request.Text ?? string.Empty;
        Multiline = request.Multiline;
        Mono = request.Mono;
        _pending = new TaskCompletionSource<string?>();
        IsOpen = true;
        return _pending.Task;
    }

    /// <summary>
    /// Закрывает диалог, оставляя настройку прежней.
    /// </summary>
    [RelayCommand]
    public void Cancel()
    {
        IsOpen = false;
        Complete(null);
    }

    // Отдаёт набранное значение строке, которая открыла диалог.
    [RelayCommand]
    private void Save()
    {
        IsOpen = false;
        Complete(Text);
    }

    private void Complete(string? result)
    {
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(result);
    }
}
