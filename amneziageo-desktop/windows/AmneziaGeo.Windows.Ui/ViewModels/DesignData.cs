using System.Threading.Tasks;
using AmneziaGeo.Windows.Ui.Services;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Design-time-only data for the Avalonia previewer. Referenced from XAML via <c>Design.DataContext</c>
/// so the previewer renders one coherent screen — the real <see cref="MainWindowViewModel"/> backed by a
/// mocked, never-started <see cref="AgentConnection"/> — instead of every IsVisible-gated layer (HOME,
/// each settings section, the toast banners) stacked on top of one another. Not constructed at runtime:
/// Avalonia strips <c>Design.*</c> assignments outside design mode, so the factory below never runs there.
/// </summary>
internal static class DesignData
{
    /// <summary>
    /// A <see cref="MainWindowViewModel"/> parked on the geo-sources settings page with sample source rows.
    /// Point <c>Design.DataContext</c> at this to preview/tweak that screen; change <c>SettingsSection</c>
    /// below (profile/config/routing/sources/logs/general) to preview a different section.
    /// </summary>
    public static MainWindowViewModel MainWindow { get; } = CreateMainWindow();

    private static MainWindowViewModel CreateMainWindow()
    {
        var vm = new MainWindowViewModel(new AgentConnection(), new UiPreferences())
        {
            Nav = "settings",
            SettingsSection = "sources",
        };

        static Task NoOp(SourceItemViewModel _) => Task.CompletedTask;
        vm.Sources.Add(new SourceItemViewModel(NoOp, NoOp)
        {
            Kind = "geosite",
            CategoryCount = 1513,
            Updated = "2026-07-05 11:47",
            Url = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
        });
        vm.Sources.Add(new SourceItemViewModel(NoOp, NoOp)
        {
            Kind = "geoip",
            CategoryCount = 260,
            Updated = "2026-07-05 11:47",
            Url = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
        });
        vm.HasSources = true;
        return vm;
    }
}
