using Android.App;
using Android.Content;
using AmneziaGeo.Android.Engine;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// Carries out what the button of the tunnel notification asks of the head: raise the selected configuration.
/// </summary>
[BroadcastReceiver(Name = TunnelNotices.ReceiverName, Exported = false, Enabled = true)]
public sealed class NoticeReceiver : BroadcastReceiver
{
    private const string Tag = "AmneziaGeoNotice";

    /// <inheritdoc/>
    public override void OnReceive(Context? context, Intent? intent)
    {
        var args = Arguments(intent);
        if (args is null)
        {
            return;
        }

        if (GoAsync() is not { } pending)
        {
            global::Android.Util.Log.Error(Tag, "the broadcast could not be held open");
            return;
        }

        if (context is not null && intent?.Action == TunnelNotices.ActionConnect)
        {
            TunnelNotices.Cancel(context, TunnelNotices.StoppedId);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var (code, text) = await CliReceiver.RunAsync(args).ConfigureAwait(false);
                global::Android.Util.Log.Info(Tag, $"{string.Join(' ', args)}: exit {code}, {text.Trim()}");
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error(Tag, ex.ToString());
            }
            finally
            {
                pending.Finish();
            }
        });
    }

    // The console command an action of the notification stands for; nothing for an action it does not know.
    private static string[]? Arguments(Intent? intent)
    {
        return intent?.Action == TunnelNotices.ActionConnect ? ["up"] : null;
    }
}
