using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Diagnostics config pane: the configuration the agent runs on (or would run on at the next connect) and the cache
/// behind it. The shown pane re-reads itself from the agent while it is on screen and stops the moment it leaves.
/// </summary>
internal sealed partial class RuntimeConfigViewModel : ViewModelBase
{
    // Cache rows rendered at most; the rest stay behind the filters.
    private const int CacheRowLimit = 1000;

    // How often the shown pane re-reads. The report costs a round of store reads plus a UAPI call, so it is only
    // ever paid while the pane is visible.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions CacheJson = new() { PropertyNameCaseInsensitive = true };

    private readonly IAgentConnection _connection;
    private readonly DispatcherTimer _poll;

    // The rows behind the rendered cache text and their total.
    private IReadOnlyList<CacheEntry> _cacheRows = [];
    private int _cacheTotal;
    private bool _cacheCapped;

    // Whether a read is in flight; a slow agent must not queue reads behind itself.
    private bool _reading;

    /// <summary>
    /// ctor
    /// </summary>
    public RuntimeConfigViewModel(IAgentConnection connection)
    {
        _connection = connection;
        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += OnPollTick;
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        Read();
    }

    private void OnCultureChanged()
    {
        if (IsActive && ShowsCacheValues)
        {
            RenderCache();
        }
    }

    /// <summary>
    /// Whether the pane is the one currently shown; gates the loads and drops late replies.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// The selectable verdicts; "all" keeps every row. The tokens are the same in every language.
    /// </summary>
    public ObservableCollection<string> CacheKinds { get; } = ["all", "state", "domain", "proxy", "direct", "block", "none"];

    // Which of the two panes is showing: the configuration itself or the caches.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsConfigValues))]
    [NotifyPropertyChangedFor(nameof(ShowsCacheValues))]
    [NotifyPropertyChangedFor(nameof(BodyText))]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string _tab = "config";

    /// <summary>
    /// Whether the configuration itself is showing.
    /// </summary>
    public bool ShowsConfigValues => Tab == "config";

    /// <summary>
    /// Whether the cache rows are showing.
    /// </summary>
    public bool ShowsCacheValues => Tab == "cache";

    partial void OnTabChanged(string value)
    {
        Read();
    }

    [RelayCommand]
    private void ShowConfigValues()
    {
        Tab = "config";
    }

    [RelayCommand]
    private void ShowCacheValues()
    {
        Tab = "cache";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyText))]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string _runtimeConfigText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyText))]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string _cacheText = string.Empty;

    // Whether the first read of a pane is in flight; stands in for a body that has nothing to show yet.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBody))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private string _cacheSummary = string.Empty;

    [ObservableProperty]
    private string _cacheFilter = string.Empty;

    [ObservableProperty]
    private string _selectedCacheKind = "all";

    partial void OnCacheFilterChanged(string value)
    {
        RenderCache();
    }

    partial void OnSelectedCacheKindChanged(string value)
    {
        RenderCache();
    }

    /// <summary>
    /// The body of the pane: the cache rows or the configuration report.
    /// </summary>
    public string BodyText => ShowsCacheValues ? CacheText : RuntimeConfigText;

    /// <summary>
    /// Whether the body is shown: there is content and no read is in flight.
    /// </summary>
    public bool ShowBody => !IsLoading && BodyText.Length > 0;

    /// <summary>
    /// Whether the empty hint is shown: no content and no read in flight.
    /// </summary>
    public bool ShowEmpty => !IsLoading && BodyText.Length == 0;

    /// <summary>
    /// Marks the pane shown or not; opening it subscribes to the agent, leaving it unsubscribes and drops what
    /// was read. Called when the section is entered or left and when the tab switches away.
    /// </summary>
    public void SetActive(bool active)
    {
        if (active == IsActive)
        {
            return;
        }

        IsActive = active;
        if (active)
        {
            Read();
            _poll.Start();
        }
        else
        {
            _poll.Stop();
            Clear();
        }
    }

    private void Clear()
    {
        _cacheRows = [];
        _cacheTotal = 0;
        _cacheCapped = false;
        RuntimeConfigText = string.Empty;
        CacheText = string.Empty;
        CacheSummary = string.Empty;
        IsLoading = false;
    }

    // Re-reads the shown pane. A read still in flight is left to finish - the next tick picks the value up.
    private void Read()
    {
        if (!IsActive || _reading)
        {
            return;
        }

        _ = ReadAsync();
    }

    private async Task ReadAsync()
    {
        _reading = true;
        try
        {
            if (ShowsCacheValues)
            {
                await LoadCacheAsync();
            }
            else
            {
                await LoadRuntimeConfigAsync();
            }
        }
        finally
        {
            _reading = false;
        }
    }

    private async Task LoadRuntimeConfigAsync()
    {
        // The loader only stands in for an empty pane; a refresh over shown text must not blank it.
        IsLoading = RuntimeConfigText.Length == 0;
        IpcAck ack;
        try
        {
            ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetRuntimeConfig, []));
        }
        catch
        {
            return;
        }
        finally
        {
            IsLoading = false;
        }

        // Pane left during the pipe round-trip: drop the reply so the freed state stays freed.
        if (!IsActive)
        {
            return;
        }

        RuntimeConfigText = ack.Ok ? ack.Message : Describe(ack);
    }

    private async Task LoadCacheAsync()
    {
        IsLoading = CacheText.Length == 0;
        IpcAck ack;
        try
        {
            ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetCacheEntries, []));
        }
        catch
        {
            return;
        }
        finally
        {
            IsLoading = false;
        }

        if (!IsActive)
        {
            return;
        }

        if (!ack.Ok)
        {
            _cacheRows = [];
            _cacheTotal = 0;
            _cacheCapped = false;
            CacheText = Describe(ack);
            CacheSummary = string.Empty;
            return;
        }

        CacheSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<CacheSnapshot>(ack.Message, CacheJson);
        }
        catch (JsonException)
        {
            return;
        }

        _cacheRows = snapshot?.Entries ?? [];
        _cacheTotal = snapshot?.Total ?? 0;
        _cacheCapped = snapshot?.Capped ?? false;
        RenderCache();
    }

    // Rebuilds the cache body from the rows the agent returned, applying the kind and text filters.
    private void RenderCache()
    {
        var needle = CacheFilter?.Trim() ?? string.Empty;
        var text = new StringBuilder();
        var matched = 0;
        var shown = 0;
        foreach (var row in _cacheRows)
        {
            if (SelectedCacheKind != "all" && !string.Equals(row.Kind, SelectedCacheKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (needle.Length > 0
                && !row.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !row.Value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched++;
            if (shown >= CacheRowLimit)
            {
                continue;
            }

            shown++;
            text.Append(row.Kind.PadRight(12)).Append(row.Key.PadRight(34)).Append(row.Value).Append('\n');
        }

        CacheText = text.ToString();
        CacheSummary = _cacheCapped
            ? Loc.Instance.Get("MainVm_CacheShownCapped", shown, matched, _cacheTotal)
            : Loc.Instance.Get("MainVm_CacheShown", shown, matched);
    }

    // Resolves a failed ack to text: the agent sends localization keys, not sentences.
    private static string Describe(IpcAck ack)
    {
        return IpcMessage.TryParse(ack.Message, out var key, out var args)
            ? Loc.Instance.Get(key, args)
            : ack.Message;
    }

    // OpGetCacheEntries ack row: which cache holds the value, its key and its content.
    private sealed record CacheEntry(string Kind, string Key, string Value);

    // OpGetCacheEntries ack payload: the rows with the total held before the agent's cap.
    private sealed record CacheSnapshot(int Total, bool Capped, IReadOnlyList<CacheEntry> Entries);
}
