using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Отказ агента словами экрана.
/// </summary>
internal static class FleetNotice
{
    /// <summary>
    /// Читает ответ агента: переводит его ключ, а незнакомое показывает как есть.
    /// </summary>
    public static string Of(IpcAck ack)
    {
        return IpcMessage.TryParse(ack.Message, out var key, out var args)
            ? Loc.Instance.Get(key, args)
            : ack.Message;
    }
}
