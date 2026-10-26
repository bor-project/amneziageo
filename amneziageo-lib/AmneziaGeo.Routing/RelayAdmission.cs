using System.Buffers.Binary;

namespace AmneziaGeo.Routing;

/// <summary>
/// Whom the relay of the Android tunnel serves: the engine of this application, which hands a stream over with a
/// header ahead of its first bytes. Any process of the device reaches the loopback port of the relay, so every other
/// connection is closed unanswered, whatever it asks.
/// </summary>
public static class RelayAdmission
{
    /// <summary>
    /// Length of the header a hand-over opens with.
    /// </summary>
    public const int HeadLength = 16;

    private const uint Mark = 0x41475354;

    /// <summary>
    /// Whether the connection belongs to this application; an owner the system does not name belongs to no one.
    /// </summary>
    public static bool FromSelf(int owner, int self) => owner >= 0 && owner == self;

    /// <summary>
    /// Whether the connection opens with the header of a hand-over.
    /// </summary>
    public static bool HandsOver(ReadOnlySpan<byte> opening) =>
        opening.Length >= HeadLength && BinaryPrimitives.ReadUInt32BigEndian(opening) == Mark;
}
