using Android.App;
using Android.Content;
using Android.Content.PM;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Result of an install session: shows the system confirmation the installer asks for, reports the terminal
/// status back to the updater and brings the window back once the package is replaced.
/// </summary>
[BroadcastReceiver(Name = "org.amneziageo.android.UpdateInstallReceiver", Exported = false)]
internal sealed class UpdateInstallReceiver : BroadcastReceiver
{
    /// <summary>
    /// Receives the outcome of the session: whether it installed and the message the installer left.
    /// </summary>
    public static Action<bool, string>? Finished { get; set; }

    /// <inheritdoc/>
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent is null)
        {
            return;
        }

        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        if (status == PackageInstallStatus.PendingUserAction)
        {
            Confirm(context, intent);
            return;
        }

        var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? status.ToString();
        var installed = status == PackageInstallStatus.Success;
        Finished?.Invoke(installed, message);
        if (installed && context is not null)
        {
            // Статус своей сессии доходит и туда, где автозапуск не пускает рассылку о замене пакета.
            UpdateReopen.Run(this, context);
        }
    }

    // The installer asks the user itself, and hands the screen over as an intent to start.
    private static void Confirm(Context? context, Intent intent)
    {
        if (intent.GetParcelableExtra(Intent.ExtraIntent) is not Intent confirm)
        {
            return;
        }

        confirm.AddFlags(ActivityFlags.NewTask);
        context?.StartActivity(confirm);
    }
}
