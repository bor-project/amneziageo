using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

/// <summary>
/// Inline selective bundle import: paste or load a bundle JSON and import it.
/// </summary>
internal sealed partial class BundleImportView : UserControl
{
    /// <summary>
    /// Extra action placed ahead of the load/import buttons in their wrap row.
    /// </summary>
    public static readonly StyledProperty<object?> LeadingActionProperty =
        AvaloniaProperty.Register<BundleImportView, object?>(nameof(LeadingAction));

    /// <summary>
    /// ctor
    /// </summary>
    public BundleImportView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Extra action placed ahead of the load/import buttons in their wrap row.
    /// </summary>
    public object? LeadingAction
    {
        get => GetValue(LeadingActionProperty);
        set => SetValue(LeadingActionProperty, value);
    }

    private async void OnLoadFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BundleImportViewModel vm)
        {
            return;
        }

        var file = await FilePickers.OpenAsync(this, Loc.Instance.Get("BundleImportCode_BundleFileTitle"), "json");
        if (file is null)
        {
            return;
        }

        try
        {
            // Read through the storage stream, not a local path: an Android picker returns a content:// URI whose
            // TryGetLocalPath is null.
            vm.Payload = await ReadAllTextAsync(file);
            vm.StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            vm.StatusMessage = ex.Message;
        }
    }

    private static async Task<string> ReadAllTextAsync(PickedFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
