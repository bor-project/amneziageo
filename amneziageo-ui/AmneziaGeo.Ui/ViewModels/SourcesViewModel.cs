using System.Collections.ObjectModel;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Geo sources screen: the source catalogue, add form, auto-check settings, and the geo-update banner.
/// </summary>
internal sealed partial class SourcesViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;
    private readonly Action<string?> _showNotice;
    private readonly Action _refreshRoutingSuggestions;

    private string _geoCategorySignature = string.Empty;
    private string? _geoBannerSignature;
    private bool _suppressSettingPush;

    // True once the current banner-initiated geo update has been observed downloading.
    private bool _geoSawActive;

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    private bool _isCompact;

    [ObservableProperty]
    private bool _geoAutoCheck = true;

    [ObservableProperty]
    private int _geoCheckIntervalHours = 24;

    [ObservableProperty]
    private string _newSourceKind = "geosite";

    [ObservableProperty]
    private string _newSourceUrl = string.Empty;

    [ObservableProperty]
    private bool _sourceKindLocked;

    [ObservableProperty]
    private bool _hasSources;

    [ObservableProperty]
    private bool _geoUpdateBannerVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeoUpdateBannerText))]
    private int _geoUpdateCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeoDownloadActive))]
    [NotifyPropertyChangedFor(nameof(ShowGeoUpdateButton))]
    private bool _geoDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GeoDownloadActive))]
    private int _geoUpdatePercent;

    /// <summary>
    /// Preset interval options (hours) for the geo auto-check combo.
    /// </summary>
    public ObservableCollection<int> GeoCheckIntervals { get; } = [6, 12, 24, 48, 168];

    /// <summary>
    /// Geo data sources.
    /// </summary>
    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];

    /// <summary>
    /// The source kinds offered in the add-source form.
    /// </summary>
    public IReadOnlyList<string> SourceKinds { get; } = ["geosite", "geoip"];

    /// <summary>
    /// ctor
    /// </summary>
    public SourcesViewModel(IAgentConnection connection, Action<string?> showNotice, Action refreshRoutingSuggestions)
    {
        _connection = connection;
        _showNotice = showNotice;
        _refreshRoutingSuggestions = refreshRoutingSuggestions;
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged()
    {
        OnPropertyChanged(nameof(GeoUpdateBannerText));
        foreach (var source in Sources)
        {
            source.RefreshLocalizedLabels();
        }
    }

    public string GeoUpdateBannerText => Loc.Instance.Get("Main_GeoUpdateBanner", GeoUpdateCount);

    /// <summary>
    /// True while a banner-initiated geo update is downloading; drives the grouped percent.
    /// </summary>
    public bool GeoDownloadActive => GeoDownloading && GeoUpdatePercent < 100;

    /// <summary>
    /// True while the banner shows the update-now button (hidden during download).
    /// </summary>
    public bool ShowGeoUpdateButton => !GeoDownloading;

    /// <summary>
    /// Applies the sources + geo-settings snapshot fields and recomputes the geo-update banner.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        _suppressSettingPush = true;
        GeoAutoCheck = snapshot.GeoAutoCheck;
        EnsureGeoInterval(snapshot.GeoCheckIntervalHours);
        GeoCheckIntervalHours = snapshot.GeoCheckIntervalHours;
        _suppressSettingPush = false;

        SyncSources(snapshot.Sources ?? []);
        ApplyGeoDownloadProgress();
        ApplyGeoUpdateBanner();
    }

    public void Reset()
    {
        Sources.Clear();
        HasSources = false;
        GeoDownloading = false;
        GeoUpdatePercent = 0;
        _geoSawActive = false;
    }

    private void SyncSources(IReadOnlyList<SourceEntry> entries)
    {
        // Reconcile in place (match by name) rather than Clear()+Add(): rebuilding the collection on
        // every snapshot push would regenerate the row controls and restart the refresh-icon spin
        // animation each tick, making it stutter. Updating fields on the existing rows keeps it smooth.
        var present = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(Sources[i].Name))
            {
                Sources.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = Sources.FirstOrDefault(s => string.Equals(s.Name, entry.Name, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new SourceItemViewModel(SendUpdateSourceAsync, SendRemoveSourceAsync, SendEditSourceAsync) { Name = entry.Name };
                Sources.Insert(Math.Min(i, Sources.Count), existing);
            }
            else
            {
                var from = Sources.IndexOf(existing);
                if (from != i)
                {
                    Sources.Move(from, i);
                }
            }

            existing.Kind = entry.Kind;
            existing.Url = entry.Url;
            existing.Updated = entry.Updated;
            existing.CategoryCount = entry.CategoryCount;
            existing.Updating = entry.Updating;
            existing.Progress = entry.Progress;
            existing.UpdateAvailable = entry.UpdateAvailable;
            existing.Error = entry.Error;
        }

        HasSources = Sources.Count > 0;

        // Refresh the open routing editor's category suggestions when the set of available categories
        // actually changed (a source finished downloading, or one was added / removed). Gated on a
        // signature so an unrelated snapshot tick (progress %, update badge) does not re-fetch list-geo.
        var signature = string.Join('|', entries
            .Select(e => $"{e.Name}={e.CategoryCount}")
            .OrderBy(s => s, StringComparer.Ordinal));
        if (signature != _geoCategorySignature)
        {
            _geoCategorySignature = signature;
            _refreshRoutingSuggestions();
        }
    }

    // Raises the geo-list update banner once per "wave": when the set of sources with a pending update
    // changes to a non-empty set the banner shows; a dismissed banner stays dismissed until that set
    // changes again; when nothing is outdated the banner hides. Sources already downloading are excluded -
    // their update is in flight, so announcing it as merely "available" would be wrong. Driven off the
    // per-source flags the snapshot already carries, so no extra round-trip is needed.
    private void ApplyGeoUpdateBanner()
    {
        var outdated = Sources
            .Where(s => s.UpdateAvailable && !s.Updating)
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        GeoUpdateCount = outdated.Count;

        // Keep the banner up while a banner-initiated geo update downloads so it can show the grouped %.
        if (GeoDownloading)
        {
            GeoUpdateBannerVisible = true;
            return;
        }

        if (outdated.Count == 0)
        {
            GeoUpdateBannerVisible = false;
            _geoBannerSignature = null;
            return;
        }

        var signature = string.Join('\n', outdated);
        if (!string.Equals(signature, _geoBannerSignature, StringComparison.Ordinal))
        {
            _geoBannerSignature = signature;
            GeoUpdateBannerVisible = true;
        }
    }

    // Rolls the per-source download percent into one grouped value while a banner-initiated geo update
    // runs, and clears the state once every source has drained.
    private void ApplyGeoDownloadProgress()
    {
        if (!GeoDownloading)
        {
            return;
        }

        var active = Sources.Where(s => s.Updating).ToList();
        if (active.Count > 0)
        {
            _geoSawActive = true;
            var known = active.Where(s => s.Progress >= 0).Select(s => s.Progress).ToList();
            var computed = known.Count > 0 ? (int)known.Average() : 0;
            GeoUpdatePercent = Math.Min(99, Math.Max(GeoUpdatePercent, computed));
            return;
        }

        // No source is downloading. Clear once we've seen the wave run, or it has settled with nothing
        // left outdated (guards a download that finishes between two snapshots).
        if (_geoSawActive || Sources.All(s => !s.UpdateAvailable))
        {
            GeoDownloading = false;
            GeoUpdatePercent = 0;
            _geoSawActive = false;
        }
    }

    [RelayCommand]
    private async Task UpdateGeoNow()
    {
        GeoDownloading = true;
        GeoUpdatePercent = 0;
        _geoSawActive = false;
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpUpdateSources, []));
    }

    [RelayCommand]
    private void DismissGeoUpdateBanner()
    {
        GeoUpdateBannerVisible = false;
        GeoDownloading = false;
        _geoSawActive = false;
    }

    partial void OnGeoAutoCheckChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("geo-auto-check", value);
        }
    }

    partial void OnGeoCheckIntervalHoursChanged(int value)
    {
        if (!_suppressSettingPush && value > 0)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting,
                ["geo-check-interval-hours", value.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        }
    }

    // Keeps the interval combo able to display whatever the agent reports: an out-of-band value (e.g. set
    // via CLI) that isn't a preset is inserted in order, so the ComboBox SelectedItem never goes null -
    // which, two-way-bound to an int, would otherwise write 0 back into the property.
    private void EnsureGeoInterval(int hours)
    {
        if (hours <= 0 || GeoCheckIntervals.Contains(hours))
        {
            return;
        }

        var index = 0;
        while (index < GeoCheckIntervals.Count && GeoCheckIntervals[index] < hours)
        {
            index++;
        }

        GeoCheckIntervals.Insert(index, hours);
    }

    private async Task SetSettingAsync(string key, bool value)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, [key, value ? "on" : "off"]));
    }

    // Infer the source kind from the URL's file name: a name containing "geosite" or "geoip" (any
    // extension) fixes the kind and locks the combo; otherwise the user picks it.
    partial void OnNewSourceUrlChanged(string value)
    {
        var detected = DetectSourceKind(value);
        if (detected is null)
        {
            SourceKindLocked = false;
            return;
        }

        SourceKindLocked = true;
        if (!string.Equals(NewSourceKind, detected, StringComparison.Ordinal))
        {
            NewSourceKind = detected;
        }
    }

    private static string? DetectSourceKind(string url)
    {
        var text = url.Trim().ToLowerInvariant();
        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var slash = text.LastIndexOf('/');
        var name = slash >= 0 ? text[(slash + 1)..] : text;
        if (name.Contains("geosite", StringComparison.Ordinal))
        {
            return "geosite";
        }

        return name.Contains("geoip", StringComparison.Ordinal) ? "geoip" : null;
    }

    [RelayCommand]
    private async Task AddSource()
    {
        var url = NewSourceUrl.Trim();
        if (url.Length == 0)
        {
            return;
        }

        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAddSource, [NewSourceKind, url]));
        NewSourceUrl = string.Empty;
    }

    // Per-row update / delete, passed as delegates to each SourceItemViewModel (the delete flyout's
    // popup can't resolve a parent-relative binding back to this view model).
    private Task SendUpdateSourceAsync(SourceItemViewModel source)
    {
        return _connection.SendCommandAsync(new IpcCommand(IpcContract.OpUpdateSource, [source.Name]));
    }

    private Task SendRemoveSourceAsync(SourceItemViewModel source)
    {
        return _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveSource, [source.Name]));
    }

    private Task SendEditSourceAsync(SourceItemViewModel source)
    {
        return _connection.SendCommandAsync(new IpcCommand(IpcContract.OpEditSource, [source.Name, source.EditKind, source.EditUrl]));
    }

    [RelayCommand]
    private async Task UpdateSources()
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpUpdateSources, []));
    }

    [RelayCommand]
    private async Task CheckSources()
    {
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpCheckSources, []));
        _showNotice(ack.Message);
    }
}
