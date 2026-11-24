namespace AmneziaGeo.Decl;

public sealed record ConfigurationUpdate(string Revision, string BaseRevision, IReadOnlyList<string> Fields)
{
    public static string Key(string name) => "configuration-update:" + name;
}
