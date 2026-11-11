using System.Diagnostics;
using System.Text.Json;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Publishes the transport of a tunnel service process.
/// </summary>
internal static class RunningTransportState
{
    private sealed record Descriptor(int ProcessId, long Started, RunningTransportProxy Transport);

    /// <summary>
    /// Stores the transport accepted by this tunnel service.
    /// </summary>
    public static async Task WriteAsync(IStateStore store, string name, RunningTransportProxy transport, ILogger logger)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var descriptor = new Descriptor(process.Id, process.StartTime.ToUniversalTime().Ticks, transport);
            await store.SetSettingAsync(Key(name, process.Id), JsonSerializer.Serialize(descriptor)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: the running transport descriptor could not be written", name);
        }
    }

    /// <summary>
    /// Reads the transport of the current tunnel service process.
    /// </summary>
    public static async Task<RunningTransportProxy?> ReadAsync(IStateStore store, string name, ServiceManager services,
        ILogger logger, CancellationToken ct)
    {
        var processId = (int)services.QueryPid(name);
        if (processId == 0)
        {
            return null;
        }

        var descriptor = await ReadDescriptorAsync(store, name, processId, logger, ct).ConfigureAwait(false);
        if (descriptor is null || descriptor.ProcessId != processId)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(descriptor.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == descriptor.Started ? descriptor.Transport : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "{Name}: the running transport process could not be read", name);
            return null;
        }
    }

    /// <summary>
    /// Removes the transport published by this tunnel service.
    /// </summary>
    public static async Task ClearAsync(IStateStore store, string name, ILogger logger)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var descriptor = await ReadDescriptorAsync(store, name, process.Id, logger, CancellationToken.None).ConfigureAwait(false);
            if (descriptor?.ProcessId == process.Id && descriptor.Started == process.StartTime.ToUniversalTime().Ticks)
            {
                await store.RemoveSettingAsync(Key(name, process.Id)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: the running transport descriptor could not be removed", name);
        }
    }

    private static string Key(string name, int processId) => "running-transport:" + name + ":" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<Descriptor?> ReadDescriptorAsync(IStateStore store, string name, int processId, ILogger logger, CancellationToken ct)
    {
        var text = await store.GetSettingAsync(Key(name, processId), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Descriptor>(text);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "{Name}: the running transport descriptor could not be read", name);
            return null;
        }
    }
}
