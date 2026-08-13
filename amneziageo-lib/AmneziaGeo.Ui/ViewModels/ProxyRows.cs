namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// One address a client points at.
/// </summary>
/// <param name="Label">Which front answers there.</param>
/// <param name="Value">The address and port, as a client takes it.</param>
internal sealed record ProxyEndpointRow(string Label, string Value);

/// <summary>
/// One client of the local proxy.
/// </summary>
/// <param name="Address">Where it dialled from.</param>
/// <param name="Detail">What it is and how long it has been there.</param>
internal sealed record ProxyClientRow(string Address, string Detail);
