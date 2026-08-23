using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Probe screen: the service a probe measures the speed against.
/// </summary>
internal sealed partial class ProbeSettingsViewModel : ViewModelBase
{
    private readonly UiPreferences _prefs;

    /// <summary>
    /// ctor
    /// </summary>
    public ProbeSettingsViewModel(UiPreferences prefs)
    {
        _prefs = prefs;
        // Seed the backing field from prefs without echoing OnChanged.
        _uploadUrl = prefs.ProbeUploadUrl;
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

    /// <summary>
    /// The service used while the field is left empty.
    /// </summary>
    public string UploadDefault => ChannelProbe.DefaultUploadUrl;
}
