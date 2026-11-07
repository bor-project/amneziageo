using System.Collections.ObjectModel;
using System.ComponentModel;
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
    public bool ShowSaveBar => IsActiveSection && HasUnsavedChanges;
    public bool CanSave => HasUnsavedChanges && !IsSaving;
    public bool CanDelete => Editor is { Editable: true, IsNew: false };
    public bool CanProbe => Editor is { Reference: not null, IsDirty: false } && !IsProbing;
    public bool IsEmpty => IsCatalogue && !IsCatalogueLoading && CatalogueError.Length == 0 && Items.Count == 0;
    public int CatalogColumns => PaneWidth > 0 && PaneWidth < UiLayout.CompactWidth ? 1 : 2;

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
        };
    }

    partial void OnIsActiveSectionChanged(bool value) => RefreshEditBar();
    partial void OnPaneWidthChanged(double value) => OnPropertyChanged(nameof(CatalogColumns));
    partial void OnIsLoadingChanged(bool value) => RefreshSections();
    partial void OnIsSavingChanged(bool value) => RefreshEditBar();
    partial void OnIsProbingChanged(bool value) => OnPropertyChanged(nameof(CanProbe));

    partial void OnEditorChanged(TransportProxyEditorViewModel? oldValue, TransportProxyEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.DirtyChanged -= OnDirtyChanged;
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.DirtyChanged += OnDirtyChanged;
            newValue.PropertyChanged += OnEditorPropertyChanged;
        }

        DeletePending = false;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(ShowEditor));
        RefreshSections();
        RefreshEditBar();
    }

    private void OnDirtyChanged(object? sender, EventArgs e) => RefreshEditBar();
    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
    }

    public void EnterSection()
    {
        DeletePending = false;
        _ = LoadAsync();
    }

    public void LeaveSection()
    {
        _returnConfig = null;
        DeletePending = false;
        if (Editor?.IsNew == true)
        {
            TryNavigateBack();
        }
    }

    public void Apply(StatusSnapshot snapshot)
    {
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
            editor.ApplySummary(summaries.FirstOrDefault(one => one.Reference == reference));
        }

        foreach (var row in Items)
        {
            row.ApplyUsage(_configs, summaries);
        }

        RefreshSections();
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
        _openGeneration++;
        IsLoading = false;
        ReturnReference = null;
        _returnConfig = null;
        Editor = new TransportProxyEditorViewModel(_connection);
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
            Editor = new TransportProxyEditorViewModel(_connection, summary: item.Summary);
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
            Editor = new TransportProxyEditorViewModel(_connection, local, item.Summary);
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
            return false;
        }

        _openGeneration++;
        Editor = null;
        IsLoading = false;
        if (_returnConfig is { } config)
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
        if (Editor is not { Reference: { } reference, IsDirty: false } editor || IsProbing)
        {
            return;
        }

        IsProbing = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpProbeTransportProxy,
                [JsonSerializer.Serialize(reference, IpcJson.Options)]));
            if (TransportProxyWords.Read<TransportProxyProbeResult>(ack) is { } result)
            {
                editor.ApplyAvailability(result);
                await LoadAsync();
            }
            else
            {
                editor.StatusMessage = TransportProxyWords.Fault(ack.Message);
            }
        }
        finally
        {
            IsProbing = false;
        }
    }

    public void Reset()
    {
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
