using Android.App;
using Android.Content;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Замена пакета, объявленная системой: возвращает окно там, где эту рассылку доставляют.
/// </summary>
[BroadcastReceiver(Name = "org.amneziageo.android.UpdateReplacedReceiver", Exported = false)]
[IntentFilter(new[] { Intent.ActionMyPackageReplaced })]
internal sealed class UpdateReplacedReceiver : BroadcastReceiver
{
    /// <inheritdoc/>
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || !string.Equals(intent?.Action, Intent.ActionMyPackageReplaced, StringComparison.Ordinal))
        {
            return;
        }

        UpdateReopen.Run(this, context);
    }
}
