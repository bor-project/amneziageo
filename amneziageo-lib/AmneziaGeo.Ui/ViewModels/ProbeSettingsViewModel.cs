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
    private readonly IAgentConnection _connection;
    private readonly UiPreferences _prefs;

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

    // What the server of the selected configuration offers, as the agent last said.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadDefault))]
    [NotifyPropertyChangedFor(nameof(UploadHint))]
    private ServerOffer _offer = ServerOffer.None;

    /// <summary>
    /// The service used while the field is left empty: the server of the tunnel where it measures itself.
    /// </summary>
    public string UploadDefault => SpeedArgs.Of(Offer) is not null ? Offer.Authority() : ChannelProbe.DefaultUploadUrl;

    /// <summary>
    /// The line under the field, naming what an empty field falls back to.
    /// </summary>
    public string UploadHint => SpeedArgs.Of(Offer) is not null
        ? Loc.Instance.Get("Probe_UploadServiceHintOwn", Offer.Config)
        : Loc.Instance.Get("Probe_UploadServiceHint");

    // Times the agent is asked again while it is still asking the servers, and the wait between them.
    private const int Attempts = 8;
    private const int GapMs = 1_000;

    // Whether a round of asking is already running, so entering the section twice does not start a second.
    private bool _asking;

    /// <summary>
    /// Asks the agent where the speed is measured. It answers out of what the servers have already said, so the
    /// screen is up at once and the question is repeated for as long as the answers are still coming in.
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
            for (var attempt = 0; attempt < Attempts && SpeedArgs.Of(Offer) is null; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(GapMs);
                }

                var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpServerOffer, []));
                if (ack.Ok)
                {
                    Offer = ServerOffer.Parse(ack.Message);
                }
            }
        }
        catch
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
