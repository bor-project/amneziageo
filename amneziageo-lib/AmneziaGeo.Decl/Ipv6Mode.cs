namespace AmneziaGeo.Decl;

public enum Ipv6Mode
{
    Auto,
    On,
    Off,
}

public static class Ipv6Modes
{
    public static Ipv6Mode Of(ConfigTransport? transport) => transport?.Ipv6Mode
        ?? (transport is null ? Ipv6Mode.Auto : transport.UseIpv6 ? Ipv6Mode.On : Ipv6Mode.Off);

    public static Ipv6Mode? Read(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" => Ipv6Mode.Auto,
        "on" or "1" or "true" or "yes" => Ipv6Mode.On,
        "off" or "0" or "false" or "no" => Ipv6Mode.Off,
        _ => null,
    };

    public static string Text(Ipv6Mode mode) => mode switch
    {
        Ipv6Mode.On => "on",
        Ipv6Mode.Off => "off",
        _ => "auto",
    };
}
