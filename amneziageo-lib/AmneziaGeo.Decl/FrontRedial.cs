namespace AmneziaGeo.Decl;

/// <summary>
/// Tells what another websocket front of its config asks of a tunnel: one that stands dialing is dialed again, and
/// the carrier of one that stands connected is started again at the front.
/// </summary>
public static class FrontRedial
{
    /// <summary>
    /// Tells whether a tunnel dialing with one front handed to it is dialed again: a front holds now, and another one.
    /// </summary>
    public static bool Wanted(bool dialing, WsEndpoint? handed, WsEndpoint? held) =>
        dialing && held is { } front && (handed is not { } before || before != front);

    /// <summary>
    /// Tells whether a carrier that stands dialing one front is started again at another: the front that holds now
    /// differs from it in the port or the path alone.
    /// </summary>
    public static bool Moved(WsEndpoint dialed, WsEndpoint? held) =>
        held is { } front && front != dialed && front == dialed with { Port = front.Port, PathPrefix = front.PathPrefix };
}
