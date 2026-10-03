using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace AmneziaGeo.Linux.Cli.Tui;

/// <summary>
/// Read-only text in the colours of the view around it; Terminal.Gui paints read-only text grey on grey.
/// </summary>
internal sealed class ReadOnlyText : TextView
{
    /// <summary>
    /// ctor
    /// </summary>
    public ReadOnlyText()
    {
        ReadOnly = true;
        TabKeyAddsTab = false;
    }

    /// <inheritdoc/>
    protected override bool OnGettingAttributeForRole(in VisualRole role, ref Attribute currentAttribute)
    {
        if (role == VisualRole.ReadOnly)
        {
            currentAttribute = GetAttributeForRole(VisualRole.Normal);
            return true;
        }

        return base.OnGettingAttributeForRole(role, ref currentAttribute);
    }
}
