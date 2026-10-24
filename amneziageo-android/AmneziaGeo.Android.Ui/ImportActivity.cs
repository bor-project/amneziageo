using Android.App;
using Android.Content;
using Android.OS;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Принимает текст и ссылки других программ (камера, браузер) и отдаёт их окну под черновик конфигурации.
/// </summary>
[Activity(
    Exported = true,
    NoHistory = true,
    ExcludeFromRecents = true,
    Theme = "@android:style/Theme.NoDisplay")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "text/plain")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "vpn")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = ImportCodec.AppScheme)]
public sealed class ImportActivity : Activity
{
    /// <summary>
    /// Дополнение намерения окна с переданным текстом.
    /// </summary>
    internal const string TextExtra = "org.amneziageo.android.extra.IMPORT_TEXT";

    /// <inheritdoc/>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var open = new Intent(this, typeof(MainActivity));
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        if (Text(Intent) is { } text)
        {
            open.PutExtra(TextExtra, text);
        }

        StartActivity(open);
        Finish();
    }

    // Текст намерения: «Поделиться» несёт его дополнением, ссылка - адресом.
    private static string? Text(Intent? intent)
    {
        var text = intent?.Action == Intent.ActionSend
            ? intent.GetStringExtra(Intent.ExtraText) ?? intent.GetCharSequenceExtra(Intent.ExtraText)?.ToString()
            : intent?.DataString;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
