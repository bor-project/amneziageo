namespace AmneziaGeo.Decl;

/// <summary>
/// Terms both sides read a dial by.
/// </summary>
public static class ConnectDial
{
    /// <summary>
    /// Dials a server is given before everything moves to the next one; the one that fell keeps trying, and a
    /// card that reached this count reads as not answering.
    /// </summary>
    public const int Attempts = 3;
}
