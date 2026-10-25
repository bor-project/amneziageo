using System.Globalization;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Слова о сертификате, из-за которого сервер держит свою подписку выключенной.
/// </summary>
internal static class CertificateWords
{
    private const string Expired = "certificate-expired";

    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        ["certificate-missing"] = "Main_CertificateMissing",
        ["certificate-unreadable"] = "Main_CertificateUnreadable",
        [Expired] = "Main_CertificateExpired",
        ["certificate-name"] = "Main_CertificateName",
        ["certificate-self-signed"] = "Main_CertificateSelfSigned",
        ["certificate-no-intermediate"] = "Main_CertificateNoIntermediate",
        ["certificate-untrusted"] = "Main_CertificateUntrusted",
        ["certificate-old-android"] = "Main_CertificateOldAndroid",
    };

    /// <summary>
    /// Называет причину словами, день окончания сертификата - датой языка окна; незнакомую причину - как есть.
    /// </summary>
    public static string Of(string fault, string detail)
    {
        if (!Keys.TryGetValue(fault, out var key))
        {
            return detail.Length > 0 ? $"{fault} ({detail})" : fault;
        }

        var shown = fault == Expired && DateTime.TryParse(detail, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var day)
            ? day.ToString("d", CultureInfo.CurrentCulture)
            : detail;

        return Loc.Instance.Get(key, shown);
    }
}
