using Android.Content;
using Android.Content.PM;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Программы устройства, которые может назвать правило по приложению.
/// </summary>
internal static class AppCensus
{
    /// <summary>
    /// Программа устройства.
    /// </summary>
    internal sealed record Entry(string Label, string Package, bool IsSystem);

    /// <summary>
    /// Перечисляет по именам программы со значком запуска или доступом в сеть, кроме этой.
    /// </summary>
    public static IReadOnlyList<Entry> List(Context context)
    {
        var manager = context.PackageManager;
        if (manager is null)
        {
            return [];
        }

        var own = context.PackageName;
        var launchable = Launchable(manager);
        return [.. Installed(manager)
            .Where(info => info.PackageName is { Length: > 0 } package
                && !string.Equals(package, own, StringComparison.Ordinal)
                && (launchable.Contains(package) || AsksNetwork(info)))
            .Select(info => new Entry(Label(info, manager), info.PackageName!, IsSystem(info) && !launchable.Contains(info.PackageName!)))
            .OrderBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Package, StringComparer.Ordinal)];
    }

    // Пакеты устройства с перечнем запрошенных разрешений.
    private static IList<PackageInfo> Installed(PackageManager manager)
    {
        return OperatingSystem.IsAndroidVersionAtLeast(33)
            ? manager.GetInstalledPackages(PackageManager.PackageInfoFlags.Of((long)PackageInfoFlags.Permissions))
            : manager.GetInstalledPackages(PackageInfoFlags.Permissions);
    }

    // Пакеты со значком запуска.
    private static HashSet<string> Launchable(PackageManager manager)
    {
        var intent = new Intent(Intent.ActionMain);
        intent.AddCategory(Intent.CategoryLauncher);
        var found = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? manager.QueryIntentActivities(intent, PackageManager.ResolveInfoFlags.Of(0L))
            : manager.QueryIntentActivities(intent, default(PackageInfoFlags));
        return [.. found
            .Select(entry => entry.ActivityInfo?.PackageName)
            .Where(package => package is { Length: > 0 })
            .Select(package => package!)];
    }

    // Просит ли пакет доступ в сеть.
    private static bool AsksNetwork(PackageInfo info)
    {
        return info.RequestedPermissions?.Contains(global::Android.Manifest.Permission.Internet) == true;
    }

    // Стоит ли пакет в системном разделе.
    private static bool IsSystem(PackageInfo info)
    {
        return info.ApplicationInfo is { } application && (application.Flags & ApplicationInfoFlags.System) != 0;
    }

    // Имя программы, а без него имя пакета.
    private static string Label(PackageInfo info, PackageManager manager)
    {
        return info.ApplicationInfo?.LoadLabel(manager)?.ToString() is { Length: > 0 } label ? label : info.PackageName!;
    }
}
