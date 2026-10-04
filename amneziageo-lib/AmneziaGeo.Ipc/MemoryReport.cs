using System.Globalization;

namespace AmneziaGeo.Ipc;

/// <summary>
/// The line the tunnel process writes about its own memory, and what the figures of the engine say.
/// </summary>
public static class MemoryReport
{
    private const double Mebibyte = 1024 * 1024;
    private const int EngineFields = 6;
    private const int HeldFreeField = 1;

    /// <summary>
    /// Composes the line from the size of the process, the managed heap and the figures of the engine.
    /// </summary>
    public static string Compose(long process, long managedUsed, long managedHeap, int minor, int major, string? engine)
    {
        return $"memory: process {Mib(process)} MiB; managed {Mib(managedUsed)} of {Mib(managedHeap)} MiB, "
            + $"{minor}/{major} collection(s); engine {Engine(engine)}";
    }

    /// <summary>
    /// Bytes the runtime of the engine holds free without giving them back; 0 when it told nothing usable.
    /// </summary>
    public static long EngineHeldFree(string? engine)
    {
        return Numbers(engine) is { } numbers ? numbers[HeldFreeField] : 0;
    }

    // Renders the figures of the engine: bytes in use, held free, returned and taken, then goroutines and collections.
    private static string Engine(string? text)
    {
        if (Numbers(text) is not { } numbers)
        {
            return "unknown";
        }

        return $"{Mib(numbers[0])} MiB in use, {Mib(numbers[1])} held free, {Mib(numbers[2])} returned, "
            + $"{Mib(numbers[3])} taken, {numbers[4]} goroutine(s), {numbers[5]} collection(s)";
    }

    // Reads the figures the engine gave; nothing when the text is not the six numbers it gives.
    private static List<long>? Numbers(string? text)
    {
        var numbers = new List<long>();
        foreach (var part in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers.Count == EngineFields ? numbers : null;
    }

    private static string Mib(long bytes) => (bytes / Mebibyte).ToString("0.0", CultureInfo.InvariantCulture);
}
