namespace AmneziaGeo.Decl;

/// <summary>
/// Tells when a tunnel that stands dialing is dialed again for the websocket front of its config.
/// </summary>
public static class FrontRedial
{
    /// <summary>
    /// Tells whether a tunnel dialing with one front handed to it is dialed again: a front holds now, and another one.
    /// </summary>
    public static bool Wanted(bool dialing, WsEndpoint? handed, WsEndpoint? held) =>
        dialing && held is { } front && (handed is not { } before || before != front);
}
