namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One address a client points at.
/// </summary>
/// <param name="Label">Which front answers there.</param>
/// <param name="Value">The address and port, as a client takes it.</param>
/// <param name="Caption">What the network of the address is; set on the row that opens its pair.</param>
internal sealed record ProxyEndpointRow(string Label, string Value, string Caption = "")
{
    /// <summary>
    /// Whether the row opens a pair that is named.
    /// </summary>
    public bool HasCaption => Caption.Length > 0;
}

/// <summary>
/// One client of the local proxy.
/// </summary>
/// <param name="Address">Where it dialled from.</param>
/// <param name="Detail">What it is and how long it has been there.</param>
internal sealed record ProxyClientRow(string Address, string Detail);
