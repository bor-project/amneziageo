using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AmneziaGeo.Android.Ui.Services;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using SharedMainView = AmneziaGeo.Ui.MainView;

namespace AmneziaGeo.Android.Ui;

/// <summary>
/// The Avalonia application.
/// </summary>
public sealed partial class App : Avalonia.Application
{
    // The shared sizes are set for a monitor: in a hand they land under what the system draws itself, and a phone
    // is further under than a tablet.
    private const double PhoneScale = 1.3;
    private const double TabletScale = 1.15;

    // The narrow side from which android calls a device a tablet.
    private const int TabletWidthDp = 600;

    private static UiPreferences? _preferences;
    private AndroidAgentConnection? _connection;

    /// <inheritdoc/>
    public override void Initialize()
    {
        var clock = Stopwatch.StartNew();
        AvaloniaXamlLoader.Load(this);
        Stage("styles", clock);
    }

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var clock = Stopwatch.StartNew();

            // Tell the shared UI which device it draws on, then drop the D-pad focus rings on a phone.
            UiPlatform.IsTelevision = global::Android.App.Application.Context.PackageManager?
                .HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureLeanback) == true;
            UiPlatform.SupportsGeoPreview = false;
            UiPlatform.UsesActionSheets = true;
            UiPlatform.UsesCompactLayout = !UiPlatform.IsTelevision;
            UiPlatform.HandScale = HandScale();
            if (!UiPlatform.IsTelevision)
            {
                Styles.Add(new StyleInclude(new Uri("avares://AmneziaGeo.Android.Ui/"))
                {
                    Source = new Uri("avares://AmneziaGeo.Ui/Themes/FlatFocus.axaml"),
                });
            }

            // Register the CameraX camera scanner so the config/routing import can scan QR codes.
            AndroidQrScanning.Register();

            // Hand an export to another application, and a QR to the clipboard, both as a file behind a link.
            AndroidExport.Register();

            // Offer an in-app exit: a TV has no window frame to close. Drops the task and the head with it, while
            // the tunnel goes on running in its own process.
            AppExitHost.Register(() => MainActivity.Current?.FinishAndRemoveTask());

            var prefs = UiPreferences.Load();
            _preferences = prefs;
            RequestedThemeVariant = prefs.Theme switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            Loc.Instance.ApplyStartupCulture(prefs.Language);
            Stage("preferences", clock);

            var connection = new AndroidAgentConnection();
            _connection = connection;
            var viewModel = new MainWindowViewModel(connection, prefs);
            Stage("view models", clock);

            var mainView = new SharedMainView
            {
                DataContext = viewModel,
            };
            Stage("views", clock);

            singleView.MainView = Framed(Enlarged(new MobileSelectHost(mainView)));
            Stage("host", clock);

            // Brings the agent up after the first frame: opening the stores and projecting the first snapshot
            // cost seconds on a TV, and home already stands on its loader until the agent answers.
            Dispatcher.UIThread.Post(() =>
            {
                var agentClock = Stopwatch.StartNew();
                viewModel.Start();
                viewModel.General.BeginAutoUpdateChecks();
                Stage("agent", agentClock);
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Holds the head clear of the status and navigation bars. From target sdk 35 android lays the window edge to
    // edge and leaves the inset to the application; the frame stands outside the scale, so it keeps the size the
    // bars are drawn at.
    private static Control Framed(Control view)
    {
        var frame = new Border
        {
            Child = view,
        };

        frame.AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(frame)?.InsetsManager is not { } insets)
            {
                return;
            }

            frame.Padding = insets.SafeAreaPadding;
            insets.SafeAreaChanged += (_, changed) => frame.Padding = changed.SafeAreaPadding;
        };

        return frame;
    }

    // Draws the head larger in the hand, layout and all. A television keeps the size its own screen was laid out at.
    private static Control Enlarged(Control view)
    {
        var scale = UiPlatform.HandScale;
        if (scale <= 1)
        {
            return view;
        }

        var scaled = new LayoutTransformControl
        {
            Child = view,
            LayoutTransform = new ScaleTransform(scale, scale),
        };

        // Weight the letters up: the shared sizes are thin on a screen held in the hand.
        TextElement.SetFontWeight(scaled, FontWeight.SemiBold);
        return scaled;
    }

    // How much over the laid-out size the head is drawn on this device.
    private static double HandScale()
    {
        if (UiPlatform.IsTelevision)
        {
            return 1;
        }

        var width = global::Android.App.Application.Context.Resources?.Configuration?.SmallestScreenWidthDp ?? 0;
        return width >= TabletWidthDp ? TabletScale : PhoneScale;
    }

    /// <summary>
    /// Takes the theme from the system night mode while the settings leave it to the system. The activity handles
    /// the ui mode change itself, so the variant is re-resolved from here and nowhere else.
    /// </summary>
    internal static void FollowSystemTheme(global::Android.Content.Res.Configuration? configuration)
    {
        if (Current is not { } app || _preferences is null || _preferences.Theme.Length > 0)
        {
            return;
        }

        var night = configuration?.UiMode & global::Android.Content.Res.UiMode.NightMask;
        var variant = night == global::Android.Content.Res.UiMode.NightYes ? ThemeVariant.Dark : ThemeVariant.Light;
        if (app.RequestedThemeVariant != variant)
        {
            app.RequestedThemeVariant = variant;
        }
    }

    // Times one startup step into logcat: a weak TV spends seconds here and only the device shows which step.
    internal static void Stage(string name, Stopwatch clock)
    {
        global::Android.Util.Log.Info("AmneziaGeoStart", $"{name} {clock.ElapsedMilliseconds} ms");
        clock.Restart();
    }
}
