namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Строка выпадающего списка правила: слово, которым набор хранит адрес, и подпись на экране.
/// </summary>
/// <param name="Word">Слово адреса.</param>
/// <param name="Text">Подпись.</param>
internal sealed record RuleTargetChoice(string Word, string Text);
