using Avalonia.Media.Imaging;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// View model for the generic QR dialog: renders a ready payload as a QR code for sharing.
/// </summary>
internal sealed partial class QrDialogViewModel : ViewModelBase
{
    /// <summary>
    /// ctor
    /// </summary>
    public QrDialogViewModel(string title, string payload, string suggestedFileName)
    {
        Title = title;
        Payload = payload;
        SuggestedFileName = suggestedFileName;
        try
        {
            QrImage = QrCodec.Generate(payload);
        }
        catch
        {
            // Payload too large for a QR.
            QrImage = null;
        }
    }

    /// <summary>
    /// Heading shown above the QR.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// The text encoded in the QR, also shown raw for copy / save.
    /// </summary>
    public string Payload { get; }

    /// <summary>
    /// Suggested file name when saving the payload.
    /// </summary>
    public string SuggestedFileName { get; }

    /// <summary>
    /// The rendered QR, or null when the payload is too large to encode.
    /// </summary>
    public Bitmap? QrImage { get; }

    /// <summary>
    /// Whether a QR was rendered.
    /// </summary>
    public bool HasQr => QrImage is not null;

    /// <summary>
    /// Whether the payload was too large to encode as a QR.
    /// </summary>
    public bool QrUnavailable => QrImage is null;

    [ObservableProperty]
    private string _statusMessage = string.Empty;
}
