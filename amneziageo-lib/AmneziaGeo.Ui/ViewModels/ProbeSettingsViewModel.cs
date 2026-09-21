using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Probe screen: the service a probe measures the speed against.
/// </summary>
internal sealed partial class ProbeSettingsViewModel : ViewModelBase
{
    // Times the agent is asked again while it is still asking the server, and the wait between them.
    private const int Attempts = 8;
    private const int GapMs = 1_000;

    private readonly IAgentConnection _connection;
    private readonly UiPreferences _prefs;

    // Whether a round of asking is already running, so entering the section twice does not start a second.
    private bool _asking;

    /// <summary>
    /// ctor
    /// </summary>
    public ProbeSettingsViewModel(IAgentConnection connection, UiPreferences prefs)
    {
        _connection = connection;
        _prefs = prefs;
        // Seed the backing field from prefs without echoing OnChanged.
        _uploadUrl = prefs.ProbeUploadUrl;
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// The service a probe uploads a test file to; empty measures against the built-in one.
    /// </summary>
    [ObservableProperty]
    private string _uploadUrl = string.Empty;

    partial void OnUploadUrlChanged(string value)
    {
        _prefs.ProbeUploadUrl = value.Trim();
        _prefs.Save();
    }

    // The selected configuration, as the agent last named it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadHint))]
    private string _offerConfig = string.Empty;

    // What the server of the selected configuration offers, as the agent last said.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadDefault))]
    [NotifyPropertyChangedFor(nameof(UploadHint))]
    private ServerOffer _offer = ServerOffer.None;

    /// <summary>
    /// The service used while the field is left empty: the server of the tunnel where it measures itself.
    /// </summary>
    public string UploadDefault =>
        Offer.Speed(false) is { } leg && Uri.TryCreate(leg.Up, UriKind.Absolute, out var up) ? up.Authority : ChannelProbe.DefaultUploadUrl;

    /// <summary>
    /// The line under the field, naming what an empty field falls back to.
    /// </summary>
    public string UploadHint => Offer.Speed(false) is not null
        ? Loc.Instance.Get("Probe_UploadServiceHintOwn", OfferConfig)
        : Loc.Instance.Get("Probe_UploadServiceHint");

    /// <summary>
    /// Asks the agent where the speed is measured. It answers out of what the server has already said, so the screen
    /// is up at once and the question is repeated for as long as the answer is still coming in.
    /// </summary>
    public void EnterSection()
    {
        _ = AskAsync();
    }

    private async Task AskAsync()
    {
        if (_asking)
        {
            return;
        }

        _asking = true;
        try
        {
            for (var attempt = 0; attempt < Attempts && Offer.Speed(false) is null; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(GapMs);
                }

                var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpServerOffer, []));
                if (ack.Ok)
                {
                    var (config, offer) = OfferReply.Parse(ack.Message);
                    OfferConfig = config;
                    Offer = offer;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            return;
        }
        finally
        {
            _asking = false;
        }
    }

    private void OnCultureChanged()
    {
        OnPropertyChanged(nameof(UploadHint));
    }
}
