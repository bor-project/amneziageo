using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

internal sealed partial class TransportProxiesViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;
    private readonly MainWindowViewModel _host;
    private int _openGeneration;
    private int _loadGeneration;
    private IReadOnlyList<ConfigEntry> _configs = [];
    private string? _returnConfig;
    private ConfigTransportViewModel? _configContext;
    private string _targetIdentity = string.Empty;
    private string? _storedEndpoint;
    private int _contextGeneration;
    private CancellationTokenSource? _catalogueCancellation;
    private IReadOnlyList<TransportProxySummary> _catalogue = [];
    private readonly Dictionary<ProxyReference, ScopedProbe> _scopedProbes = [];
    private sealed record ScopedProbe(string Definition, string Target, TransportProxyProbeResult Result);
    public string Title => Editor?.Title ?? Loc.Instance.Get("Proxies_Title");
    public string ConfigurationName => _returnConfig ?? string.Empty;
    public bool CanAdd => _configContext is not null && ConfigurationName.Length > 0 && !IsRefreshing && !IsCheckingAll;
    public bool IsScoped => _configContext is not null;
    public bool IsCompact => PaneWidth > 0 && PaneWidth < UiLayout.CompactWidth;
    public bool CanRefresh => IsCatalogue && IsScoped && ConfigurationName.Length > 0 && !IsCatalogueLoading && !IsRefreshing && !IsCheckingAll;
    public bool CanCheckAll => CanRefresh && Items.Any(one => _configContext?.AllowsProxy(one.Summary) == true);
    [ObservableProperty]
    private bool _isRefreshing;
    [ObservableProperty]
    private bool _isCheckingAll;
    [ObservableProperty]
    private bool _isCatalogueLoading;
    [ObservableProperty]
    private string _catalogueError = string.Empty;
    public bool ShowCatalogueError => IsCatalogue && CatalogueError.Length > 0;
    partial void OnIsCatalogueLoadingChanged(bool value) => RefreshSections();
    partial void OnCatalogueErrorChanged(string value) => RefreshSections();

    public ObservableCollection<TransportProxyItemViewModel> Items { get; } = [];
    public ProxyReference? ReturnReference { get; private set; }
    public bool IsCatalogue => Editor is null && !IsLoading;
    public bool ShowEditor => Editor is not null;
    public bool HasUnsavedChanges => Editor?.IsDirty == true;
    public string UnsavedName => Editor?.Name ?? string.Empty;
    public bool ShowSaveBar => IsActiveSection && Editor?.Editable == true;
    public bool CanSave => Editor is { Editable: true, IsVerified: true } && !IsSaving;
    public bool CanDelete => Editor is { Editable: true, IsNew: false };
    public bool CanProbe => Editor is not null && ConfigurationName.Length > 0 && !IsProbing && !IsRefreshing && !IsCheckingAll;
    public bool IsEmpty => IsCatalogue && !IsCatalogueLoading && CatalogueError.Length == 0 && Items.Count == 0;
    public int CatalogColumns => IsCompact ? 1 : 2;

    [ObservableProperty]
    private TransportProxyEditorViewModel? _editor;
    [ObservableProperty]
    private bool _isActiveSection;
    [ObservableProperty]
    private bool _isLoading;
    [ObservableProperty]
    private bool _isSaving;
    [ObservableProperty]
    private bool _isProbing;
    [ObservableProperty]
    private bool _deletePending;
    [ObservableProperty]
    private string _statusMessage = string.Empty;
    [ObservableProperty]
    private double _paneWidth;

    /// <summary>
    /// ctor
    /// </summary>
    public TransportProxiesViewModel(MainWindowViewModel host, IAgentConnection connection)
    {
        _host = host;
        _connection = connection;
        Loc.Instance.CultureChanged += () =>
        {
            foreach (var item in Items)
            {
                item.Refresh();
            }

            Editor?.RefreshWords();
            OnPropertyChanged(nameof(Title));
        };
    }

    partial void OnIsActiveSectionChanged(bool value) => RefreshEditBar();
    partial void OnPaneWidthChanged(double value)
    {
        OnPropertyChanged(nameof(CatalogColumns));
        OnPropertyChanged(nameof(IsCompact));
        if (Editor is { } editor)
        {
            editor.Compact = value < UiLayout.FieldRowWidth;
        }
    }
    partial void OnIsLoadingChanged(bool value) => RefreshSections();
    partial void OnIsSavingChanged(bool value) => RefreshEditBar();
    partial void OnIsProbingChanged(bool value) => OnPropertyChanged(nameof(CanProbe));
    partial void OnIsRefreshingChanged(bool value) => RefreshActions();
    partial void OnIsCheckingAllChanged(bool value) => RefreshActions();

    partial void OnEditorChanged(TransportProxyEditorViewModel? oldValue, TransportProxyEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.InvalidateVerification();
            oldValue.DirtyChanged -= OnDirtyChanged;
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.Compact = PaneWidth < UiLayout.FieldRowWidth;
            newValue.DirtyChanged += OnDirtyChanged;
            newValue.PropertyChanged += OnEditorPropertyChanged;
        }

        DeletePending = false;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(Title));
        RefreshSections();
        RefreshEditBar();
    }

    private void OnDirtyChanged(object? sender, EventArgs e) => RefreshEditBar();
    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransportProxyEditorViewModel.IsVerified))
        {
            RefreshEditBar();
        }
        if (e.PropertyName == nameof(TransportProxyEditorViewModel.Title))
        {
            OnPropertyChanged(nameof(Title));
        }
        if (e.PropertyName == nameof(TransportProxyEditorViewModel.Name))
        {
            OnPropertyChanged(nameof(UnsavedName));
        }
    }

    private void RefreshEditBar()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedName));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanProbe));
    }

    private void RefreshSections()
    {
        OnPropertyChanged(nameof(IsCatalogue));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowCatalogueError));
        RefreshActions();
    }

    private void RefreshActions()
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCheckAll));
        OnPropertyChanged(nameof(CanProbe));
        RefreshCommand.NotifyCanExecuteChanged();
        CheckAllCommand.NotifyCanExecuteChanged();
    }

    private void CancelCatalogueOperation()
    {
        _catalogueCancellation?.Cancel();
        _catalogueCancellation = null;
        IsRefreshing = false;
        IsCheckingAll = false;
        IsProbing = false;
    }

    private void InvalidateProbeScope()
    {
        _contextGeneration++;
        CancelCatalogueOperation();
        _scopedProbes.Clear();
        if (_catalogue.Count > 0)
        {
            Apply(_catalogue);
        }
    }

    public void EnterSection()
    {
        DeletePending = false;
        _ = LoadAsync();
    }

    public void LeaveSection()
    {
        Editor?.InvalidateVerification();
        DetachContext();
        _returnConfig = null;
        DeletePending = false;
        if (Editor?.IsNew == true)
        {
            TryNavigateBack();
        }
    }

    public void Apply(StatusSnapshot snapshot)
    {
        if (_configContext is { } context)
        {
            var endpoint = snapshot.Configs.FirstOrDefault(one => one.Name == context.ConfigName)?.Endpoint;
            if (_storedEndpoint is not null && _storedEndpoint != endpoint)
            {
                Editor?.InvalidateVerification();
                InvalidateProbeScope();
            }
            _storedEndpoint = endpoint;
        }
        _configs = snapshot.Configs;
        if (snapshot.TransportProxies is { } summaries)
        {
            Apply(summaries);
        }

        foreach (var row in Items)
        {
            row.ApplyUsage(_configs, Items.Select(one => one.Summary).ToArray());
        }
    }

    private void Apply(IReadOnlyList<TransportProxySummary> summaries)
    {
        _catalogue = summaries;
        foreach (var probeReference in _scopedProbes.Keys.ToArray())
        {
            if (!summaries.Any(one => one.Reference == probeReference && !one.Missing
                && Definition(one) == _scopedProbes[probeReference].Definition && _scopedProbes[probeReference].Target == _targetIdentity))
            {
                _scopedProbes.Remove(probeReference);
            }
        }
        summaries = summaries.Select(WithScopedProbe).ToArray();
        _configContext?.RefreshProxyCatalog(summaries);
        var offered = summaries;
        summaries = _configContext is null ? summaries : summaries.Where(one => _configContext.AllowsProxy(one)
            || one.Reference == _configContext.SelectedProxyChoice?.Reference).ToArray();
        var present = summaries.Select(one => one.Reference).ToHashSet();
        for (var at = Items.Count - 1; at >= 0; at--)
        {
            if (!present.Contains(Items[at].Reference))
            {
                Items.RemoveAt(at);
            }
        }

        for (var at = 0; at < summaries.Count; at++)
        {
            var summary = summaries[at];
            var row = Items.FirstOrDefault(one => one.Reference == summary.Reference);
            if (row is null)
            {
                Items.Insert(Math.Min(at, Items.Count), new TransportProxyItemViewModel(summary));
            }
            else
            {
                row.Apply(summary);
                var old = Items.IndexOf(row);
                if (old != at)
                {
                    Items.Move(old, at);
                }
            }
        }

        if (Editor is { Reference: { } reference } editor)
        {
            editor.ApplySummary(offered.FirstOrDefault(one => one.Reference == reference));
        }

        foreach (var row in Items)
        {
            row.ApplyUsage(_configs, summaries);
            row.SelectionChanged = SelectProxy;
        }
        RefreshSelection();

        RefreshSections();
    }

    private static string Definition(TransportProxySummary summary) => summary.DefinitionStamp.Length > 0
        ? summary.DefinitionStamp
        : JsonSerializer.Serialize(summary with { Availability = TransportProxyAvailability.Unknown, Fault = string.Empty, Checked = null, Diagnostic = null }, IpcJson.Options);

    private TransportProxySummary WithScopedProbe(TransportProxySummary summary) => _scopedProbes.TryGetValue(summary.Reference, out var probe)
        ? summary with { Availability = probe.Result.Availability, Fault = probe.Result.Fault, Checked = probe.Result.Checked, Diagnostic = probe.Result.Diagnostic }
        : summary;

    private void KeepScopedProbe(TransportProxySummary summary, TransportProxyProbeResult result, int generation, string target)
    {
        if (generation != _contextGeneration || target != _targetIdentity
            || Items.FirstOrDefault(one => one.Reference == summary.Reference) is not { } row
            || row.Summary.Missing || Definition(row.Summary) != Definition(summary))
        {
            return;
        }

        var checkedResult = result with { Checked = result.Checked ?? DateTimeOffset.UtcNow };
        _scopedProbes[summary.Reference] = new(Definition(summary), target, checkedResult);
        row.Apply(WithScopedProbe(row.Summary));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task Refresh()
    {
        if (!CanRefresh)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _catalogueCancellation = cancellation;
        var generation = _contextGeneration;
        IsRefreshing = true;
        CatalogueError = string.Empty;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAskServers, [ConfigurationName])).WaitAsync(cancellation.Token);
            if (generation != _contextGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }
            if (TransportProxyWords.Read<List<TransportProxySummary>>(ack) is { } summaries)
            {
                Apply(summaries);
            }
            else if (TransportProxyWords.RefreshResult(ack) is { } result)
            {
                Apply(result.Items);
                CatalogueError = TransportProxyWords.RefreshFault(result);
            }
            else
            {
                await LoadAsync();
                if (generation == _contextGeneration)
                {
                    CatalogueError = TransportProxyWords.Fault(ack.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _contextGeneration)
            {
                CatalogueError = Loc.Instance.Get("Proxies_RequestFailed");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("{0}", ex);
            if (generation == _contextGeneration)
            {
                CatalogueError = Loc.Instance.Get("Proxies_RequestFailed");
            }
        }
        finally
        {
            if (ReferenceEquals(_catalogueCancellation, cancellation))
            {
                _catalogueCancellation = null;
                IsRefreshing = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckAll))]
    private async Task CheckAll()
    {
        if (!CanCheckAll || _configContext is not { } context)
        {
            return;
        }

        var candidates = Items.Where(one => context.AllowsProxy(one.Summary)).Select(one => one.Summary).ToArray();
        var config = ConfigurationName;
        var target = context.ProxyDestination;
        var identity = _targetIdentity;
        var generation = _contextGeneration;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var slots = new SemaphoreSlim(2, 2);
        _catalogueCancellation = cancellation;
        IsCheckingAll = true;
        CatalogueError = string.Empty;
        try
        {
            var results = await Task.WhenAll(candidates.Select(CheckOneAsync));
            foreach (var (summary, result) in results)
            {
                if (result is not null)
                {
                    KeepScopedProbe(summary, result, generation, identity);
                }
            }
            if (cancellation.IsCancellationRequested && generation == _contextGeneration)
            {
                CatalogueError = Loc.Instance.Get("Proxies_RequestFailed");
            }
        }
        finally
        {
            if (ReferenceEquals(_catalogueCancellation, cancellation))
            {
                _catalogueCancellation = null;
                IsCheckingAll = false;
            }
        }

        async Task<(TransportProxySummary Summary, TransportProxyProbeResult? Result)> CheckOneAsync(TransportProxySummary summary)
        {
            try
            {
                await slots.WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return (summary, null);
            }

            try
            {
                var args = new List<string> { JsonSerializer.Serialize(summary.Reference, IpcJson.Options), config };
                if (target is not null)
                {
                    args.Add(target.Host);
                    args.Add(target.Port.ToString(CultureInfo.InvariantCulture));
                }
                var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpProbeTransportProxy, args)).WaitAsync(cancellation.Token).ConfigureAwait(false);
                var result = TransportProxyWords.Read<TransportProxyProbeResult>(ack)
                    ?? new(TransportProxyAvailability.Unavailable, ack.Ok ? "proxy-bad-answer" : ack.Message);
                return (summary, result);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return (summary, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("{0}", ex);
                return (summary, new(TransportProxyAvailability.Unavailable, "proxy-no-answer"));
            }
            finally
            {
                slots.Release();
            }
        }
    }

    [RelayCommand]
    private Task Retry() => LoadAsync();

    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        IsCatalogueLoading = true;
        CatalogueError = string.Empty;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListTransportProxies, []));
            if (generation != _loadGeneration)
            {
                return;
            }

            if (TransportProxyWords.Read<List<TransportProxySummary>>(ack) is { } summaries)
            {
                Apply(summaries);
            }
            else
            {
                CatalogueError = TransportProxyWords.Fault(ack.Message);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("{0}", ex);
            if (generation == _loadGeneration)
            {
                CatalogueError = Loc.Instance.Get("Proxies_RequestFailed");
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsCatalogueLoading = false;
            }
        }
    }

    [RelayCommand]
    private void Add()
    {
        if (!CanAdd)
        {
            return;
        }
        _openGeneration++;
        IsLoading = false;
        ReturnReference = null;
        Editor = new TransportProxyEditorViewModel(_connection, ownerConfig: ConfigurationName);
    }

    public void OpenForConfiguration(ConfigTransportViewModel context)
    {
        DetachContext();
        _configContext = context;
        _targetIdentity = context.ProxyTargetIdentity;
        _storedEndpoint = _configs.FirstOrDefault(one => one.Name == context.ConfigName)?.Endpoint;
        context.PropertyChanged += OnContextPropertyChanged;
        _returnConfig = context.ConfigName;
        Editor = null;
        OnPropertyChanged(nameof(ConfigurationName));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(IsScoped));
        Apply(_catalogue);
        _ = LoadAsync();
    }

    private void DetachContext()
    {
        if (_configContext is { } context)
        {
            context.PropertyChanged -= OnContextPropertyChanged;
        }
        _configContext = null;
        _storedEndpoint = null;
        _loadGeneration++;
        IsCatalogueLoading = false;
        InvalidateProbeScope();
        OnPropertyChanged(nameof(IsScoped));
        OnPropertyChanged(nameof(CanAdd));
    }

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigTransportViewModel.StatusMessage))
        {
            CatalogueError = _configContext?.StatusMessage ?? string.Empty;
        }
        if (e.PropertyName is nameof(ConfigTransportViewModel.SelectedProxyChoice) or nameof(ConfigTransportViewModel.SelectedProxyEnabled))
        {
            RefreshSelection();
        }
        if (_configContext is { } context && _targetIdentity != context.ProxyTargetIdentity)
        {
            _targetIdentity = context.ProxyTargetIdentity;
            Editor?.InvalidateVerification();
            InvalidateProbeScope();
        }
    }

    private void SelectProxy(TransportProxyItemViewModel item, bool enabled)
    {
        _configContext?.SelectFromPicker(item.Summary, enabled);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var row in Items)
        {
            row.ApplySelection(_configContext?.SelectedProxyChoice?.Reference, true,
                _configContext?.AllowsProxy(row.Summary) == true);
        }
    }

    public async Task OpenReferenceAsync(ProxyReference reference, string? returnConfig)
    {
        _returnConfig = returnConfig;
        var summary = TransportProxyPresentation.FindRunning(Items.Select(one => one.Summary).ToArray(), reference);
        if (Items.FirstOrDefault(one => one.Reference == summary?.Reference) is { } row)
        {
            await OpenCard(row);
        }
    }

    [RelayCommand]
    private async Task OpenCard(TransportProxyItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var generation = ++_openGeneration;
        ReturnReference = item.Reference;
        StatusMessage = string.Empty;
        if (!item.Summary.Local || item.Summary.Missing)
        {
            Editor = new TransportProxyEditorViewModel(_connection, summary: item.Summary, ownerConfig: ConfigurationName);
            return;
        }

        IsLoading = true;
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetTransportProxy,
            [JsonSerializer.Serialize(item.Reference, IpcJson.Options)]));
        if (generation != _openGeneration)
        {
            return;
        }

        IsLoading = false;
        if (TransportProxyWords.Read<CatalogTransportProxy>(ack) is { Local: true } local)
        {
            Editor = new TransportProxyEditorViewModel(_connection, local, item.Summary, ConfigurationName);
        }
        else
        {
            StatusMessage = TransportProxyWords.Fault(ack.Message);
        }
    }

    public bool TryNavigateBack()
    {
        if (Editor is null && !IsLoading)
        {
            if (_configContext is null || _returnConfig is not { } owner)
            {
                return false;
            }
            DetachContext();
            _returnConfig = null;
            _host.ReturnFromTransportProxy(owner);
            return true;
        }

        _openGeneration++;
        CancelCatalogueOperation();
        Editor = null;
        IsLoading = false;
        if (_configContext is null && _returnConfig is { } config)
        {
            _returnConfig = null;
            _host.ReturnFromTransportProxy(config);
        }

        return true;
    }

    [RelayCommand]
    private async Task SaveSection()
    {
        if (Editor is not { } editor || IsSaving)
        {
            return;
        }

        IsSaving = true;
        try
        {
            var reconnect = editor.ConnectionChanged && _host.Config.UsesRunningProxy(editor.Reference)
                || _host.Config.RunningProxyTargetChanged(editor.Reference, editor.TargetPermissionChanged);
            if (await editor.CommitAsync())
            {
                ReturnReference = editor.Reference;
                if (reconnect)
                {
                    _host.ArmReconnectPrompt();
                }

                await LoadAsync();
                TryNavigateBack();
            }
        }
        finally
        {
            IsSaving = false;
            RefreshEditBar();
        }
    }

    [RelayCommand]
    private void CancelSection()
    {
        if (Editor?.IsNew == true)
        {
            TryNavigateBack();
            return;
        }

        Editor?.Revert();
        DeletePending = false;
        RefreshEditBar();
    }

    [RelayCommand]
    private void RequestDelete() => DeletePending = true;

    [RelayCommand]
    private void CancelDelete() => DeletePending = false;

    [RelayCommand]
    private async Task Delete()
    {
        if (Editor is not { Editable: true, Reference: { } reference } || IsSaving)
        {
            return;
        }

        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveTransportProxy,
            [JsonSerializer.Serialize(reference, IpcJson.Options)]));
        if (!ack.Ok)
        {
            StatusMessage = TransportProxyWords.Fault(ack.Message);
            return;
        }

        TryNavigateBack();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Probe()
    {
        if (Editor is not { } editor || IsProbing || !CanProbe)
        {
            return;
        }

        IsProbing = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _catalogueCancellation = cancellation;
        var generation = _contextGeneration;
        var identity = _targetIdentity;
        var summary = Items.FirstOrDefault(one => one.Reference == editor.Reference)?.Summary;
        try
        {
            await editor.CheckAsync(ConfigurationName, _configContext?.ProxyDestination).WaitAsync(cancellation.Token);
            if (Editor == editor && !editor.IsDirty && summary is not null && editor.LastProbeResult is { } result)
            {
                KeepScopedProbe(summary, result, generation, identity);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_catalogueCancellation, cancellation))
            {
                _catalogueCancellation = null;
                IsProbing = false;
            }
        }
    }

    public void Reset()
    {
        _catalogue = [];
        DetachContext();
        _openGeneration++;
        _loadGeneration++;
        _returnConfig = null;
        _configs = [];
        IsCatalogueLoading = false;
        CatalogueError = string.Empty;
        Editor = null;
        Items.Clear();
        IsLoading = false;
        RefreshSections();
    }
}
