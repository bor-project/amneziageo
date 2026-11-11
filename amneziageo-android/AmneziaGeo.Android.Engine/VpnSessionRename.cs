namespace AmneziaGeo.Android.Engine;

public sealed record VpnSessionLabel(string SessionId, string Name);

public sealed record VpnSessionRename(string RequestId, string SessionId, string OldName, string NewName);

public sealed record VpnSessionRenameAck(string RequestId, string SessionId, bool Applied);
