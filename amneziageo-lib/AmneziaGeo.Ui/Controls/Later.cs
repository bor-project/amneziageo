using Avalonia.Controls;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Metadata;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Строит своё содержимое при первом показе.
/// </summary>
internal sealed class Later : Decorator
{
    private bool _built;

    /// <summary>
    /// Содержимое, которое строится при первом показе.
    /// </summary>
    [Content]
    [TemplateContent]
    public object? Content { get; set; }

    /// <inheritdoc/>
    public override void ApplyTemplate()
    {
        if (_built)
        {
            return;
        }

        _built = true;
        Child = TemplateContent.Load(Content)?.Result;
    }
}
