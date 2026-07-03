namespace AmneziaGeo.Localization;

/// <summary>
/// Marker type that names the resource set for the Microsoft localizer: its full name
/// (<c>AmneziaGeo.Localization.Strings</c>) is the base name of <c>Strings.resx</c> / <c>Strings.ru.resx</c>,
/// so <c>IStringLocalizerFactory.Create(typeof(Strings))</c> resolves them. Intentionally empty - all lookups
/// go through <see cref="Loc"/> / <c>IStringLocalizer</c> by key, not through generated members.
/// </summary>
public sealed class Strings
{
    private Strings()
    {
    }
}
