namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One line of the list of the update sources.
/// </summary>
/// <param name="Title">What the line says.</param>
/// <param name="Address">The address under it, empty where it shows none.</param>
internal sealed record UpdateSourceOption(string Title, string Address)
{
    /// <summary>
    /// Whether the line shows the address.
    /// </summary>
    public bool HasAddress => Address.Length > 0;

    /// <inheritdoc />
    public override string ToString() => Title;
}
