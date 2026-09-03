namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Строка списка мест туннеля: место в цепочке и подпись на экране.
/// </summary>
/// <param name="Slot">Место.</param>
/// <param name="Text">Подпись.</param>
internal sealed record TunnelSlotChoice(int Slot, string Text);
