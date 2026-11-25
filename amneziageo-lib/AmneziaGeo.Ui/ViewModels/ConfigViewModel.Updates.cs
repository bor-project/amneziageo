using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

internal partial class ConfigViewModel
{
    private readonly Dictionary<string, ConfigurationUpdate> _configurationUpdates = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyOpenConfigurationUpdate))]
    private bool _applyingConfigurationUpdate;

    [ObservableProperty]
    private string _configurationUpdateError = string.Empty;

    public bool CanApplyOpenConfigurationUpdate => !ApplyingConfigurationUpdate && !HasUnsavedChanges;

    public bool HasOpenConfigurationUpdate => OpenConfig is { } name && _configurationUpdates.ContainsKey(name);

    public string OpenConfigurationUpdateFields => OpenConfig is { } name && _configurationUpdates.TryGetValue(name, out var update)
        ? string.Join(", ", update.Fields) : string.Empty;

    private void DescribeConfigurationUpdates(IReadOnlyList<ConfigEntry> entries)
    {
        _configurationUpdates.Clear();
        foreach (var entry in entries.Where(one => one.Update is not null))
        {
            _configurationUpdates[entry.Name] = entry.Update!;
        }
        NotifyConfigurationUpdate();
    }

    private void NotifyConfigurationUpdate()
    {
        OnPropertyChanged(nameof(HasOpenConfigurationUpdate));
        OnPropertyChanged(nameof(OpenConfigurationUpdateFields));
    }

    /// <summary>
    /// Applies one confirmed configuration update.
    /// </summary>
    public async Task ApplyConfigurationUpdateAsync(string name, string revision, string baseRevision)
    {
        if (ApplyingConfigurationUpdate || HasUnsavedChanges)
        {
            return;
        }
        ApplyingConfigurationUpdate = true;
        ConfigurationUpdateError = string.Empty;
        try
        {
            var ack = await Ask(new IpcCommand(IpcContract.OpApplyConfigUpdate, [name, revision, baseRevision]));
            if (ack is not { Ok: true })
            {
                ConfigurationUpdateError = Loc.Instance.Get("Main_ConfigurationUpdateFailed");
            }
            else
            {
                if (_configurationUpdates.TryGetValue(name, out var pending) && pending.Revision == revision && pending.BaseRevision == baseRevision)
                {
                    _configurationUpdates.Remove(name);
                }
                if (OpenConfig == name)
                {
                    if (ConfigExport is { } export)
                    {
                        await export.LoadAsync();
                    }
                }
                NotifyConfigurationUpdate();
            }
        }
        finally
        {
            ApplyingConfigurationUpdate = false;
        }
    }

    [RelayCommand]
    private Task ApplyOpenConfigurationUpdate() => OpenConfig is { } name && _configurationUpdates.TryGetValue(name, out var update)
        ? ApplyConfigurationUpdateAsync(name, update.Revision, update.BaseRevision) : Task.CompletedTask;
}
