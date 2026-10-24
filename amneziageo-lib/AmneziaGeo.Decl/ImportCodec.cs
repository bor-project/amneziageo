namespace AmneziaGeo.Decl;

/// <summary>
/// Что несёт добавляемый текст.
/// </summary>
public enum ImportKind
{
    Unknown,
    Subscription,
    Config,
}

/// <summary>
/// Разбирает добавляемый текст: адрес подписки, конфигурация или ни то ни другое.
/// </summary>
public static class ImportCodec
{
    /// <summary>
    /// Разобранное: адрес у подписки, конфигурация у конфигурации.
    /// </summary>
    public readonly record struct Recognized(ImportKind Kind, string Address, VpnLinkCodec.Imported? Config);

    /// <summary>
    /// Схема ссылки, которой страница подписки открывает приложение: amneziageo://import?url=адрес подписки.
    /// </summary>
    public const string AppScheme = "amneziageo";

    /// <summary>
    /// Разбирает текст из файла, буфера обмена или поля ввода.
    /// </summary>
    public static Recognized Recognize(string? text)
    {
        return Classify(text, VpnLinkCodec.TryDecode);
    }

    /// <summary>
    /// Разбирает текст, снятый с QR.
    /// </summary>
    public static Recognized RecognizeQr(string? text)
    {
        return Classify(text, VpnLinkCodec.TryDecodeQr);
    }

    // Адрес подписки проверяется первым: ссылка на конфигурацию несёт свою схему, а не http.
    private static Recognized Classify(string? text, Func<string, VpnLinkCodec.Imported?> decode)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new Recognized(ImportKind.Unknown, string.Empty, null);
        }

        if (AppLinkAddress(trimmed) is { } address)
        {
            return new Recognized(ImportKind.Subscription, address, null);
        }

        if (SubscriptionCodec.LooksLikeAddress(trimmed))
        {
            return new Recognized(ImportKind.Subscription, trimmed, null);
        }

        return decode(trimmed) is { } imported
            ? new Recognized(ImportKind.Config, string.Empty, imported)
            : new Recognized(ImportKind.Unknown, string.Empty, null);
    }

    // Адрес подписки из ссылки приложения.
    private static string? AppLinkAddress(string text)
    {
        if (!text.StartsWith(AppScheme + "://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "import", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("url=", StringComparison.Ordinal))
            {
                var address = Uri.UnescapeDataString(pair["url=".Length..]).Trim();
                return SubscriptionCodec.LooksLikeAddress(address) ? address : null;
            }
        }

        return null;
    }
}
