using System.Text.Json;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Shared JSON options for the pipe protocol.
/// </summary>
public static class IpcJson
{
    /// <summary>
    /// The serializer options used on both ends of the pipe.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
