using System;
using System.IO;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using AmneziaGeo.Ui.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AmneziaGeo.Ui.Design;

/// <summary>
/// Снимает секцию «Подключения» во всех состояниях. Оснастка для обзора, приложением не вызывается.
/// </summary>
public static class Shots
{
    private const double Wide = 760;
    private const double Narrow = 420;
    private const double Scale = 2;
    private const double Pad = 14;
    private const double HostWidth = 800;
    private const double HostHeight = 2400;
    private const string ProxyTab = "proxy";
    private const string WifiTab = "wifi";

    private static string _log = string.Empty;
    private static Window? _host;

    private static readonly StatusSnapshot _base = new("1.5.9.0", null, Array.Empty<ConfigEntry>())
    {
        ProxyEnabled = true,
        ProxyRunning = true,
        ProxyAddresses = new[] { "192.168.1.47", "192.168.1.12" },
        ProxyCredentials = "guest:letmein12" + "\n" + "tv:kitchen-99",
        ShareMode = ShareModes.Both,
        HotspotSupported = true,
        HotspotMaxClients = 8,
        HotspotSsid = "WORK-PC 6202",
        HotspotPassword = "sunny-lake-42",
        HotspotBand = HotspotBands.Auto,
    };

    /// <summary>
    /// Пишет снимки состояний в каталог.
    /// </summary>
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        _log = Path.Combine(dir, "shots.log");
        File.WriteAllText(_log, "start" + Environment.NewLine);

        try
        {
            Loc.Instance.ApplyStartupCulture("ru");
            Note("culture applied");

            DropSegmentShadow();

            Shot(dir, "01-proksi-vykluchen", Wide, ThemeVariant.Light, _base with { ProxyEnabled = false });
            Shot(dir, "02-proksi", Wide, ThemeVariant.Light, _base);
            Shot(dir, "03-proksi-bez-parolya", Wide, ThemeVariant.Light, _base with { ProxyAnonymous = true });
            Shot(dir, "04-tochka-vykluchena", Wide, ThemeVariant.Light, _base with { ShareMode = ShareModes.Lan }, WifiTab);
            Shot(dir, "05-tochka-net-imeni", Wide, ThemeVariant.Light, _base with { ShareMode = ShareModes.Wifi, HotspotSsid = "", HotspotPassword = "" }, WifiTab);
            Shot(dir, "06-tochka-gotova", Wide, ThemeVariant.Light, _base with { ShareMode = ShareModes.Wifi }, WifiTab);
            Shot(dir, "07-tochka-rabotaet", Wide, ThemeVariant.Light, _base with { ShareMode = ShareModes.Wifi, HotspotRunning = true, HotspotClients = 3, HotspotBandActual = HotspotBands.Five }, WifiTab);
            Shot(dir, "08-tochka-bez-proksi", Wide, ThemeVariant.Light, _base with { ProxyEnabled = false, ShareMode = ShareModes.Wifi, HotspotRunning = true, HotspotClients = 3, HotspotBandActual = HotspotBands.Five }, WifiTab);
            Shot(dir, "09-net-adaptera", Wide, ThemeVariant.Light, _base with { ShareMode = ShareModes.Wifi, HotspotSupported = false, HotspotReason = HotspotReasons.NoAdapter }, WifiTab);
            Shot(dir, "10-radio-off", Wide, ThemeVariant.Light, _base with { ShareMode = ShareModes.Wifi, HotspotSupported = false, HotspotReason = HotspotReasons.RadioOff }, WifiTab);
            Shot(dir, "11-ne-umeet-tochku", Wide, ThemeVariant.Light, _base with { ShareMode = ShareModes.Wifi, HotspotSupported = false, HotspotReason = HotspotReasons.NoApMode }, WifiTab);
            Shot(dir, "12-oshibka", Wide, ThemeVariant.Light, _base with { ShareMode = ShareModes.Wifi, HotspotError = "hostapd: nl80211 driver initialization failed" }, WifiTab);
            Shot(dir, "13-temnaya", Wide, ThemeVariant.Dark, _base with { ShareMode = ShareModes.Wifi, HotspotRunning = true, HotspotClients = 2, HotspotBandActual = HotspotBands.Five }, WifiTab);
            Shot(dir, "14-uzkiy-proksi", Narrow, ThemeVariant.Light, _base);
            Shot(dir, "15-uzkiy-tochka", Narrow, ThemeVariant.Light, _base with { ShareMode = ShareModes.Wifi, HotspotRunning = true, HotspotClients = 2, HotspotBandActual = HotspotBands.Five }, WifiTab);
        }
        catch (Exception ex)
        {
            Note("FAILED " + ex);
        }

        Note("done");
    }

    // Тень активного сегмента в offscreen-рендере закрашивает его подпись.
    private static void DropSegmentShadow()
    {
        var style = new Style(x => x.OfType<Button>().Class("seg").Class("active")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"));
        style.Setters.Add(new Setter(ContentPresenter.BoxShadowProperty, default(BoxShadows)));
        Application.Current?.Styles.Add(style);
    }

    private static void Shot(string dir, string name, double width, ThemeVariant theme, StatusSnapshot snapshot, string tab = ProxyTab)
    {
        Note(name + " begin");

        var app = Application.Current;
        if (app != null)
        {
            app.RequestedThemeVariant = theme;
        }

        var vm = new MainWindowViewModel(new NullAgentConnection(), new UiPreferences());
        vm.General.Apply(snapshot);
        vm.General.SelectShareTabCommand.Execute(tab);

        var view = new GeneralView
        {
            DataContext = vm.General,
            Width = width,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };

        // Подложка красит фон темы: по прозрачному тексту сглаживание рисует ореолы.
        var page = new Border
        {
            Background = Background(app, theme),
            Padding = new Thickness(16),
            Child = view,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };

        // Страница живёт в прокрутке: окно ограничено высотой экрана, а в прокрутке она мерится целиком.
        var host = Host();
        host.Content = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Pump(host);
        Note(name + " laid out " + page.Bounds.Width + "x" + page.Bounds.Height);

        var section = view.FindControl<StackPanel>("ShareSection");
        if (section == null)
        {
            Note(name + " ShareSection not found");
            return;
        }

        // Рисуется сама страница, а не окно: окно обрезано высотой экрана.
        var sectionOrigin = section.TranslatePoint(new Point(0, 0), page) ?? new Point();

        var left = 0d;
        var top = Math.Max(0, sectionOrigin.Y - Pad);
        var cropW = page.Bounds.Width;
        var cropH = Math.Min(page.Bounds.Height - top, section.Bounds.Height + (Pad * 2));

        using var full = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(page.Bounds.Width * Scale), (int)Math.Ceiling(page.Bounds.Height * Scale)),
            new Vector(96 * Scale, 96 * Scale));
        full.Render(page);

        var pixelW = (int)Math.Ceiling(cropW * Scale);
        var pixelH = (int)Math.Ceiling(cropH * Scale);
        using var crop = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(96, 96));
        using (var context = crop.CreateDrawingContext())
        {
            context.DrawImage(
                full,
                new Rect(left * Scale, top * Scale, pixelW, pixelH),
                new Rect(0, 0, pixelW, pixelH));
        }

        crop.Save(Path.Combine(dir, name + ".png"));
        Note(name + " saved " + pixelW + "x" + pixelH);
    }

    // Фон страницы из темы приложения.
    private static Avalonia.Media.IBrush Background(Application? app, ThemeVariant theme)
    {
        if (app != null && app.TryFindResource("AgBgBrush", theme, out var found) && found is Avalonia.Media.IBrush brush)
        {
            return brush;
        }

        return Avalonia.Media.Brushes.White;
    }

    // Одно показанное окно на все снимки: скрытое рисуется пустым, а показ с закрытием на каждом кадре вешают процесс.
    private static Window Host()
    {
        if (_host == null)
        {
            _host = new Window
            {
                SystemDecorations = SystemDecorations.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Position = new PixelPoint(-6000, 0),
                Padding = new Thickness(16),
                Width = HostWidth,
                Height = HostHeight,
            };

            _host.Show();
            Pump(_host);
            Note("host shown");
        }

        return _host;
    }

    private static void Pump(Window host)
    {
        Dispatcher.UIThread.RunJobs();
        host.InvalidateMeasure();
        host.Measure(new Size(HostWidth, HostHeight));
        host.Arrange(new Rect(0, 0, HostWidth, HostHeight));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Note(string text)
    {
        File.AppendAllText(_log, text + Environment.NewLine);
    }
}
