namespace AmneziaGeo.Android.Engine;

internal static class VpnRestartMarker
{
    public static bool Required(string path, string sessionId) =>
        sessionId.Length > 0 && File.Exists(path) && File.ReadAllText(path) == sessionId;

    public static void Mark(string path, string sessionId)
    {
        if (sessionId.Length == 0 || Required(path, sessionId))
        {
            return;
        }

        var fresh = path + ".new";
        File.WriteAllText(fresh, sessionId);
        File.Move(fresh, path, true);
    }

    public static void AcceptSession(string path, string sessionId)
    {
        if (sessionId.Length > 0 && !Required(path, sessionId))
        {
            Clear(path);
        }
    }

    public static void Clear(string path) => File.Delete(path);
}
