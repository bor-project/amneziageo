using Android.Content;
using AndroidX.Core.Content;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Отдаёт выгрузку другому приложению и кладёт картинку в буфер обмена: и то и другое едет ссылкой на файл.
/// </summary>
internal static class AndroidExport
{
    // Provider authority declared in the manifest.
    private const string Authority = "org.amneziageo.android.files";

    /// <summary>
    /// Регистрирует пути отправки в общем UI.
    /// </summary>
    public static void Register()
    {
        PlatformExportHost.Register(SendAsync, CopyImageAsync);
    }

    // Открывает системный выбор приложения.
    private static Task<bool> SendAsync(string name, string mime, byte[] content)
    {
        if (Publish(name, content) is not { } uri || MainActivity.Current is not { } activity)
        {
            return Task.FromResult(false);
        }

        var intent = new Intent(Intent.ActionSend)
            .SetType(mime)
            .PutExtra(Intent.ExtraStream, uri)
            .PutExtra(Intent.ExtraSubject, name)
            .AddFlags(ActivityFlags.GrantReadUriPermission);
        if (Intent.CreateChooser(intent, name) is not { } chooser)
        {
            return Task.FromResult(false);
        }

        chooser.AddFlags(ActivityFlags.GrantReadUriPermission);
        activity.RunOnUiThread(() => activity.StartActivity(chooser));
        return Task.FromResult(true);
    }

    // Кладёт картинку в буфер обмена ссылкой на файл.
    private static Task<bool> CopyImageAsync(string name, byte[] png)
    {
        var context = global::Android.App.Application.Context;
        if (Publish(name, png) is not { } uri
            || context.ContentResolver is not { } resolver
            || context.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard)
        {
            return Task.FromResult(false);
        }

        clipboard.PrimaryClip = ClipData.NewUri(resolver, name, uri);
        return Task.FromResult(true);
    }

    // Пишет содержимое в кеш и выдаёт ссылку провайдера.
    private static global::Android.Net.Uri? Publish(string name, byte[] content)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            if (context.CacheDir?.AbsolutePath is not { } cache)
            {
                return null;
            }

            var folder = System.IO.Path.Combine(cache, "share");
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, Safe(name));
            System.IO.File.WriteAllBytes(path, content);
            return FileProvider.GetUriForFile(context, Authority, new Java.IO.File(path));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("AndroidExport", "publishing the export failed: " + ex);
            return null;
        }
    }

    // Имя файла без разделителей пути.
    private static string Safe(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? "export.txt" : trimmed.Replace('/', '-').Replace('\\', '-');
    }
}
