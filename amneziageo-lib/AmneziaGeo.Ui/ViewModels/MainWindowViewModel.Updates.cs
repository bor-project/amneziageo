using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

namespace AmneziaGeo.Ui.ViewModels;

internal sealed partial class MainWindowViewModel
{
    internal void OfferConfigurationUpdates(IReadOnlyList<ConfigEntry> entries)
    {
        if (Sheet.IsOpen || Config.HasUnsavedChanges || Config.ApplyingConfigurationUpdate)
        {
            return;
        }
        var next = entries.FirstOrDefault(one => one.Update is { } update
            && ConfigUpdateSkips.Waits(_prefs.SkippedConfigUpdates, "config:" + one.Name, update.Revision));
        if (next?.Update is not { } offered)
        {
            return;
        }
        _prefs.SkippedConfigUpdates = ConfigUpdateSkips.Skip([("config:" + next.Name, offered.Revision)], _prefs.SkippedConfigUpdates);
        _prefs.Save();
        Sheet.Show(Loc.Instance.Get("Main_ConfigurationUpdateTitle", next.Name), string.Join(", ", offered.Fields),
        [
            new ActionOption(Loc.Instance.Get("Main_ConfigUpdateApply"), () => Glyphs.Refresh,
                () => { _ = Config.ApplyConfigurationUpdateAsync(next.Name, offered.Revision, offered.BaseRevision); }),
            new ActionOption(Loc.Instance.Get("Main_ConfigurationUpdateLater"), () => Glyphs.Close, () => { }),
        ]);
    }
}
