using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Editor for a shared routing list: name + rules (geo categories or manual domains / cidrs). Edits stay in
/// memory and are persisted on an explicit "Сохранить"; that same action (re)builds the share QR. Nothing is
/// regenerated while typing - auto-saving each keystroke churned the list catalogue (renaming → snapshot →
/// rebuilt picker) and the live QR swapped the Image, both of which stole focus from the inputs.
/// </summary>
internal sealed partial class RoutingListEditorViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly Action<long>? _onSaved;
    private long _id;

    // The share QR is built only on Save. _qrFresh marks that the shown QR (or its absence) reflects the
    // last Save, so editing hides a now-stale QR / "too large" note until the user saves again.
    private bool _qrFresh;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _ruleInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQr))]
    [NotifyPropertyChangedFor(nameof(QrUnavailable))]
    private Bitmap? _qrImage;

    /// <summary>
    /// ctor used when creating a fresh routing list.
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, Action<long>? onSaved = null)
    {
        _connection = connection;
        _onSaved = onSaved;
        _id = 0;
        IsNew = true;
        Rules.CollectionChanged += (_, _) => InvalidateQr();
    }

    /// <summary>
    /// ctor used when editing an existing routing list (rules loaded via LoadAsync).
    /// </summary>
    public RoutingListEditorViewModel(AgentConnection connection, long id, string name, Action<long>? onSaved = null)
    {
        _connection = connection;
        _onSaved = onSaved;
        _id = id;
        Rules.CollectionChanged += (_, _) => InvalidateQr();
        Name = name;
        IsNew = false;
    }

    /// <summary>
    /// True when this editor is creating a new list rather than editing an existing one.
    /// </summary>
    public bool IsNew { get; }

    /// <summary>
    /// The persisted list id (0 until a new list is first saved).
    /// </summary>
    public long Id => _id;

    /// <summary>
    /// The rules of this list as rule tokens (geosite:openai etc).
    /// </summary>
    public ObservableCollection<string> Rules { get; } = [];

    /// <summary>
    /// Geo category suggestions for the rule input, fetched from the agent.
    /// </summary>
    public ObservableCollection<string> GeoSuggestions { get; } = [];

    /// <summary>Whether a QR code is currently shown (built by the last Save).</summary>
    public bool HasQr => QrImage is not null;

    /// <summary>
    /// Whether the last Save could not encode the list as a QR (too large). Only meaningful right after a
    /// Save; cleared as soon as the user edits.
    /// </summary>
    public bool QrUnavailable => _qrFresh && QrImage is null;

    /// <summary>
    /// Fetches geo category suggestions and (for existing lists) the current rules.
    /// </summary>
    public async Task LoadAsync()
    {
        var suggestions = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListGeo, []));
        if (suggestions.Ok)
        {
            foreach (var token in suggestions.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                GeoSuggestions.Add(token);
            }
        }

        if (_id != 0)
        {
            var detail = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetRoutingList, [_id.ToString(CultureInfo.InvariantCulture)]));
            if (detail.Ok)
            {
                foreach (var token in detail.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Rules.Add(token);
                }
            }
        }
    }

    /// <summary>
    /// "Сохранить": persists the list (insert or update) through the agent and, on success, (re)builds the
    /// share QR. The QR is produced here only - never live while typing.
    /// </summary>
    [RelayCommand]
    private async Task Save()
    {
        if (!await SaveAsync())
        {
            return;
        }

        _onSaved?.Invoke(_id);
        RefreshQr();
        _qrFresh = true;
        OnPropertyChanged(nameof(QrUnavailable));
    }

    /// <summary>
    /// Saves the list (insert or update) through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        var trimmed = Name.Trim();
        if (trimmed.Length == 0)
        {
            StatusMessage = "Введите имя правила";
            return false;
        }

        IsBusy = true;
        try
        {
            var args = new List<string> { _id.ToString(CultureInfo.InvariantCulture), trimmed };
            args.AddRange(Rules);
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSaveRoutingList, args));
            if (ack.Ok && long.TryParse(ack.Message, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resultId))
            {
                _id = resultId;
            }

            StatusMessage = ack.Ok ? "Сохранено" : ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Deletes the list through the agent; returns whether it succeeded.
    /// </summary>
    public async Task<bool> DeleteAsync()
    {
        if (_id == 0)
        {
            return true;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveRoutingList, [_id.ToString(CultureInfo.InvariantCulture)]));
            StatusMessage = ack.Message;
            return ack.Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        var text = RuleInput.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var rule = Normalize(text);
        if (!Rules.Contains(rule))
        {
            Rules.Add(rule);
        }

        RuleInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveRule(string rule)
    {
        Rules.Remove(rule);
    }

    /// <summary>
    /// Clears all entries of this list at once, for rebuilding a large list from scratch (persist with Save).
    /// </summary>
    [RelayCommand]
    private void ClearRules()
    {
        Rules.Clear();
    }

    /// <summary>A suggested file name when exporting this list.</summary>
    public string SuggestedFileName => string.IsNullOrWhiteSpace(Name) ? "routing.txt" : $"{Name.Trim()}-routing.txt";

    /// <summary>
    /// Serialises this list (name + rules) to a portable blob for copy / save - the same share flow a
    /// config has.
    /// </summary>
    public string BuildTransferPayload() => PortableTransfer.EncodeRouting(Name, Rules);

    /// <summary>
    /// Replaces this list's name + rules from an imported blob. Returns whether the text was a recognisable
    /// routing-list blob; persist the result with Save.
    /// </summary>
    public bool ApplyImport(string text)
    {
        if (!PortableTransfer.TryDecodeRouting(text, out var name, out var importedRules))
        {
            StatusMessage = "Не похоже на список маршрутизации.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            Name = name;
        }

        Rules.Clear();
        foreach (var rule in importedRules)
        {
            Rules.Add(rule);
        }

        StatusMessage = $"Импортировано правил: {importedRules.Count}. Нажмите «Сохранить».";
        return true;
    }

    partial void OnNameChanged(string value)
    {
        InvalidateQr();
    }

    // Editing invalidates the shown QR: drop it (and the "too large" note) until the next explicit Save.
    private void InvalidateQr()
    {
        _qrFresh = false;
        QrImage = null;
        OnPropertyChanged(nameof(QrUnavailable));
    }

    // Build the share QR from the current name + rules; null when the payload is too large to encode.
    private void RefreshQr()
    {
        try
        {
            QrImage = QrCodec.Generate(BuildTransferPayload());
        }
        catch
        {
            QrImage = null;
        }
    }

    private static string Normalize(string text)
    {
        if (text.Contains(':'))
        {
            return text;
        }

        return text.Contains('/') ? $"cidr:{text}" : $"domain:{text}";
    }
}
