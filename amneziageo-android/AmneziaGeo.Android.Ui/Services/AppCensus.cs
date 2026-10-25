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
    internal sealed record Entry(string Label, string Package);

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
                && (launchable.Contains(package) || HoldsNetwork(manager, package)))
            .Select(info => new Entry(Label(info, manager), info.PackageName!))
            .OrderBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Package, StringComparer.Ordinal)];
    }

    // Пакеты устройства.
    private static IList<PackageInfo> Installed(PackageManager manager)
    {
        return OperatingSystem.IsAndroidVersionAtLeast(33)
            ? manager.GetInstalledPackages(PackageManager.PackageInfoFlags.Of(0L))
            : manager.GetInstalledPackages(default(PackageInfoFlags));
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

    // Есть ли у пакета доступ в сеть.
    private static bool HoldsNetwork(PackageManager manager, string package)
    {
        return manager.CheckPermission(global::Android.Manifest.Permission.Internet, package) == Permission.Granted;
    }

    // Имя программы, а без него имя пакета.
    private static string Label(PackageInfo info, PackageManager manager)
    {
        return info.ApplicationInfo?.LoadLabel(manager)?.ToString() is { Length: > 0 } label ? label : info.PackageName!;
    }
}
